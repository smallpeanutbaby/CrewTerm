using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace CrewTerm.Automation;

/// <summary>Runs user commands with bounded parallelism and retained output.</summary>
public sealed class TaskRunner
{
    private readonly SemaphoreSlim slots;
    private readonly int maxOutputCharacters;

    public TaskRunner(int maxConcurrency = 4, int maxOutputCharacters = 262144)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputCharacters);
        slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        this.maxOutputCharacters = maxOutputCharacters;
    }

    public async Task<TaskRunResult> RunAsync(CommandTask task, Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.WorkingDirectory);
        var directory = Path.GetFullPath(task.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            return new(id, task.Name, task.Command, directory, now, now, null, "", true);
        }

        try
        {
            var started = DateTimeOffset.UtcNow;
            using var process = new Process { StartInfo = CreateStartInfo(task.Command, directory) };
            var output = new StringBuilder();
            var outputLock = new object();
            var truncated = false;
            ExceptionDispatchInfo? callbackError = null;
            if (!process.Start()) throw new InvalidOperationException("The command could not be started.");
            process.StandardInput.Close();
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });

            async Task PumpAsync(StreamReader reader)
            {
                var buffer = new char[4096];
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    var chunk = new string(buffer, 0, length);
                    lock (outputLock)
                    {
                        output.Append(chunk);
                        if (output.Length > maxOutputCharacters)
                        {
                            output.Remove(0, output.Length - maxOutputCharacters);
                            truncated = true;
                        }
                        if (callbackError is null)
                        {
                            try { onOutput?.Invoke(chunk); }
                            catch (Exception exception) { callbackError = ExceptionDispatchInfo.Capture(exception); }
                        }
                    }
                }
            }

            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            callbackError?.Throw();
            return new(id, task.Name, task.Command, directory, started, DateTimeOffset.UtcNow,
                process.ExitCode, output.ToString(), cancellationToken.IsCancellationRequested, truncated);
        }
        finally
        {
            slots.Release();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string directory)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh",
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            // cmd has its own quoting grammar. ArgumentList applies CRT backslash
            // escaping, which turns quotes inside a shell command into literal \".
            // The command is intentional user shell input, not an interpolated path.
            // cmd caches its redirected-output encoding at startup. Set the console
            // code page, then launch the command in a fresh shell that sees UTF-8.
            start.Arguments = "/d /s /c \"chcp 65001 >nul & \"" + start.FileName
                + "\" /d /s /c \"" + command + "\"\"";
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        return start;
    }
}
