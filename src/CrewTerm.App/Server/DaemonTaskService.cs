using CrewTerm.Automation;
using System.Text;

namespace CrewTerm.App.Server;

public sealed record DaemonTaskInfo(string Id, string Name, string Command, string WorkingDirectory,
    DateTimeOffset StartedAt, bool Completed, int? ExitCode, bool Cancelled, string Output, string? Error);

/// <summary>Owns background command lifetimes independently of any connected TUI client.</summary>
public sealed class DaemonTaskService : IDisposable
{
    private const int OutputLimit = 262144;
    private sealed class Entry(DaemonTaskInfo info, CancellationTokenSource? cancellation)
    {
        public DaemonTaskInfo Info = info;
        public readonly CancellationTokenSource? Cancellation = cancellation;
        public readonly StringBuilder Output = new(info.Output);
        public Task Completion = Task.CompletedTask;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TaskRunner runner;
    private readonly TaskHistory history;
    private readonly SemaphoreSlim admission;
    private bool stopping;
    private bool disposed;
    private Task? stopTask;

    public DaemonTaskService(string stateDirectory, int maximumConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        runner = new TaskRunner(maximumConcurrency, OutputLimit);
        admission = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        history = new TaskHistory(stateDirectory);
        foreach (var result in history.List())
        {
            var error = result.ExitCode is null && !result.Cancelled ? "Task failed before reporting an exit code; see output." : null;
            entries[result.Id] = new(new(result.Id, result.Name, result.Command, result.WorkingDirectory,
                result.StartedAt, true, result.ExitCode, result.Cancelled, Tail(result.Output), error), null);
        }
    }

    public string Start(CommandTask command)
    {
        command = ValidateCommand(command);
        return StartCore(command.Name, command.Command, command.WorkingDirectory,
            entry => runner.RunAsync(command, text => AppendOutput(entry, text), entry.Cancellation!.Token));
    }

    public string StartPipeline(string name, IReadOnlyList<CommandTask> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) throw new ArgumentException("A pipeline must contain at least one command.", nameof(commands));
        var steps = commands.Select(ValidateCommand).ToArray();
        var description = string.Join(Environment.NewLine, steps.Select(step => $"{step.Name}: {step.Command}"));
        return StartCore(name, description, steps[0].WorkingDirectory, async entry =>
        {
            var result = await new PipelineRunner(runner).RunAsync(steps,
                text => AppendOutput(entry, text), entry.Cancellation!.Token).ConfigureAwait(false);
            var last = result.Steps.LastOrDefault();
            string output;
            lock (gate) output = entry.Output.ToString();
            return new(entry.Info.Id, name, description, steps[0].WorkingDirectory,
                result.Steps.FirstOrDefault()?.StartedAt ?? entry.Info.StartedAt,
                last?.FinishedAt ?? DateTimeOffset.UtcNow, last?.ExitCode, output, result.Cancelled,
                result.Steps.Any(step => step.OutputTruncated));
        });
    }

    public DaemonTaskInfo[] List()
    {
        lock (gate) return entries.Values.Select(entry => entry.Info)
            .OrderByDescending(info => info.StartedAt).ToArray();
    }

    public DaemonTaskInfo? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate) return entries.TryGetValue(id, out var entry) ? entry.Info : null;
    }

    public bool Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(id, out var entry) || entry.Info.Completed || entry.Cancellation is null)
                return false;
            _ = entry.Cancellation.CancelAsync();
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null) return stopTask;
            stopping = true;
            var active = entries.Values.Where(entry => !entry.Info.Completed).ToArray();
            stopTask = StopJobsAsync(active);
            return stopTask;
        }
    }

    private static async Task StopJobsAsync(Entry[] active)
    {
        var cancellations = active.Where(entry => entry.Cancellation is not null)
            .Select(entry => entry.Cancellation!.CancelAsync()).ToArray();
        await Task.WhenAll(cancellations).ConfigureAwait(false);
        await Task.WhenAll(active.Select(entry => entry.Completion)).ConfigureAwait(false);
    }

    private string StartCore(string name, string command, string directory, Func<Entry, Task<TaskRunResult>> execute)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping) throw new InvalidOperationException("The task service is stopping.");
            var id = Guid.NewGuid().ToString("N");
            var info = new DaemonTaskInfo(id, name, command, directory, DateTimeOffset.UtcNow, false, null, false, "", null);
            var entry = new Entry(info, new CancellationTokenSource());
            entries.Add(id, entry);
            entry.Completion = Task.Run(() => ExecuteAsync(entry, execute));
            return id;
        }
    }

    private async Task ExecuteAsync(Entry entry, Func<Entry, Task<TaskRunResult>> execute)
    {
        TaskRunResult result;
        string? error = null;
        var admitted = false;
        try
        {
            await admission.WaitAsync(entry.Cancellation!.Token).ConfigureAwait(false);
            admitted = true;
            lock (gate)
                if (stopping) throw new OperationCanceledException("The task service is stopping.");
            result = await execute(entry).ConfigureAwait(false);
            result = result with { Id = entry.Info.Id };
        }
        catch (OperationCanceledException)
        {
            result = FailedResult(entry, cancelled: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.Message;
            AppendOutput(entry, Environment.NewLine + "[CrewTerm] " + error + Environment.NewLine);
            result = FailedResult(entry, cancelled: false);
        }
        finally
        {
            if (admitted) admission.Release();
        }

        try { history.Add(result); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"Task finished, but its history could not be saved: {exception.Message}";
        }
        lock (gate)
        {
            entry.Info = entry.Info with
            {
                Completed = true, ExitCode = result.ExitCode, Cancelled = result.Cancelled,
                Output = Tail(result.Output), Error = error
            };
            entry.Output.Clear();
        }
    }

    private TaskRunResult FailedResult(Entry entry, bool cancelled)
    {
        lock (gate)
            return new(entry.Info.Id, entry.Info.Name, entry.Info.Command, entry.Info.WorkingDirectory,
                entry.Info.StartedAt, DateTimeOffset.UtcNow, null, entry.Output.ToString(), cancelled);
    }

    private void AppendOutput(Entry entry, string output)
    {
        lock (gate)
        {
            entry.Output.Append(output);
            if (entry.Output.Length > OutputLimit) entry.Output.Remove(0, entry.Output.Length - OutputLimit);
            entry.Info = entry.Info with { Output = entry.Output.ToString() };
        }
    }

    private static CommandTask ValidateCommand(CommandTask command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkingDirectory);
        var directory = Path.GetFullPath(command.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        return command with { WorkingDirectory = directory };
    }

    private static string Tail(string text) => text.Length <= OutputLimit ? text : text[^OutputLimit..];

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        StopAsync().GetAwaiter().GetResult();
        lock (gate)
        {
            foreach (var entry in entries.Values) entry.Cancellation?.Dispose();
            admission.Dispose();
        }
    }
}
