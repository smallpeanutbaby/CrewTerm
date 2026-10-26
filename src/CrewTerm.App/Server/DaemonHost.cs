using CrewTerm.Core;
using CrewTerm.Automation;
using CrewTerm.Terminal;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CrewTerm.App.Server;

public sealed class DaemonHost : IDisposable
{
    private sealed record Running(ConPtySession Terminal, string Name, DateTimeOffset StartedAt);
    private readonly string stateDirectory;
    private readonly ConcurrentDictionary<string, Running> sessions = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly object changeLock = new();
    private readonly SemaphoreSlim connectionSlots = new(16, 16);
    private readonly DaemonTaskService taskService;
    private bool disposed;
    private bool runStarted;

    public DaemonHost(string stateDirectory)
    {
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(this.stateDirectory);
        var settings = new WorkspaceSettingsStore(this.stateDirectory).Load();
        taskService = new DaemonTaskService(this.stateDirectory, settings.MaximumConcurrentTasks);
    }

    public async Task RunAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        runStarted = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        using var processLock = new FileStream(Path.Combine(stateDirectory, "server.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var handlers = new List<Task>();
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(linked.Token);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(DaemonClient.PipeName(stateDirectory), PipeDirection.InOut,
                        16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(linked.Token);
                }
                catch { pipe?.Dispose(); connectionSlots.Release(); throw; }
                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(HandleConnectionSlotAsync(pipe, linked.Token));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(handlers); }
            finally
            {
                try { await taskService.StopAsync(); }
                finally { connectionSlots.Dispose(); }
            }
        }
    }

    private async Task HandleConnectionSlotAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try { await HandleConnectionAsync(pipe, token); }
        finally { connectionSlots.Release(); }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await Protocol.ReadLineAsync(reader, Protocol.MaxRequest, timeout.Token);
                DaemonResponse response;
                DaemonRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<DaemonRequest>(line, Protocol.Json);
                    response = request is null ? new(false, "Empty request") : Execute(request);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { response = new(false, error.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.Json).AsMemory(), timeout.Token);
                if (request?.Operation == "stop" && response.Ok) await shutdown.CancelAsync();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        }
    }

    private DaemonResponse Execute(DaemonRequest request)
    {
        switch (request.Operation)
        {
            case "ping": return new(true, Text: "CrewTerm 0.1.0");
            case "list": return new(true, Sessions: ListSessions());
            case "stop": return new(true);
            case "task-start":
                ValidateTaskCommand(request.Command);
                var taskId = taskService.Start(new CommandTask(
                    string.IsNullOrWhiteSpace(request.Name) ? request.Command! : request.Name,
                    request.Command!, request.WorkingDirectory ?? stateDirectory));
                return new(true, SessionId: taskId);
            case "pipeline-start":
                if (request.Steps is null || request.Steps.Length == 0)
                    throw new ArgumentException("At least one pipeline step is required");
                foreach (var step in request.Steps)
                {
                    ArgumentNullException.ThrowIfNull(step);
                    ValidateTaskCommand(step.Command);
                }
                var pipelineId = taskService.StartPipeline(
                    string.IsNullOrWhiteSpace(request.Name) ? "Pipeline" : request.Name, request.Steps);
                return new(true, SessionId: pipelineId);
            case "task-list":
                return new(true, Tasks: taskService.List().Select(info => info with { Output = "" }).ToArray());
            case "task-get":
                var task = request.SessionId is null ? null : taskService.Get(request.SessionId);
                return task is null ? new(false, "Task not found") : new(true, Task: task);
            case "task-cancel":
                if (request.SessionId is null || !taskService.Cancel(request.SessionId))
                    return new(false, "Task not found or already completed");
                return new(true);
            case "create":
                ValidateSize(request.Columns, request.Rows);
                if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("Command is required");
                if (request.Command.Length > 8192) throw new ArgumentException("Command is too long");
                var cwd = Path.GetFullPath(request.WorkingDirectory ?? stateDirectory);
                if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
                lock (changeLock)
                {
                    if (sessions.Count >= 32) throw new InvalidOperationException("Maximum 32 sessions; close unused sessions first");
                    var id = Guid.NewGuid().ToString("N")[..12];
                    var terminal = ConPtySession.Start(id, request.Command, cwd, request.Columns, request.Rows);
                    sessions[id] = new(terminal, string.IsNullOrWhiteSpace(request.Name) ? request.Command : request.Name,
                        DateTimeOffset.UtcNow);
                    try { SaveTemplates(); }
                    catch
                    {
                        sessions.TryRemove(id, out _);
                        terminal.Dispose();
                        throw;
                    }
                    return new(true, SessionId: id);
                }
            case "close":
                if (request.SessionId is null || !sessions.TryRemove(request.SessionId, out var closing))
                    return new(false, "Session not found");
                closing.Terminal.Dispose();
                lock (changeLock) SaveTemplates();
                return new(true);
            case "screen":
                var screen = Find(request.SessionId).Terminal.Screen;
                return new(true, Screen: screen.Snapshot(), Text: screen.GetText());
            case "input":
                var input = Find(request.SessionId).Terminal;
                if (input.HasExited) return new(false, "Session has exited");
                input.Write(request.Text ?? "");
                return new(true);
            case "resize":
                ValidateSize(request.Columns, request.Rows);
                Find(request.SessionId).Terminal.Resize(request.Columns, request.Rows);
                return new(true);
            default: return new(false, "Unknown operation: " + request.Operation);
        }
    }

    private Running Find(string? id) => id is not null && sessions.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException("Session not found");

    private SessionInfo[] ListSessions() => sessions.Select(pair => new SessionInfo(pair.Key, pair.Value.Name,
        pair.Value.Terminal.CommandLine, pair.Value.Terminal.WorkingDirectory, pair.Value.StartedAt,
        pair.Value.Terminal.HasExited, pair.Value.Terminal.ExitCode)).OrderBy(s => s.StartedAt).ToArray();

    private void SaveTemplates() => AtomicJsonStore.Save(Path.Combine(stateDirectory, "saved-sessions.json"), ListSessions());

    private static void ValidateTaskCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required");
        if (command.Length > 8192) throw new ArgumentException("Command is too long");
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 2 or > 300 || rows is < 2 or > 120)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size must be 2–300 columns and 2–120 rows");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown.Cancel();
        taskService.Dispose();
        foreach (var session in sessions.Values) session.Terminal.Dispose();
        sessions.Clear();
        shutdown.Dispose();
        // Running handlers still release their slots while cancellation unwinds.
        // RunAsync owns disposal after those handlers have drained.
        if (!runStarted) connectionSlots.Dispose();
    }
}
