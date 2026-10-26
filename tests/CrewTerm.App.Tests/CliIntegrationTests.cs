using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Server;

namespace CrewTerm.App.Tests;

public sealed class CliIntegrationTests : IAsyncLifetime
{
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermCliTests", Guid.NewGuid().ToString("N"));
    private readonly string executable;
    private string Project => Path.Combine(state, "project with spaces 项目");

    public CliIntegrationTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrewTerm.slnx")))
            directory = directory.Parent;
        var repository = directory?.FullName ?? throw new DirectoryNotFoundException("Source repository not found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        executable = Path.Combine(repository, "src", "CrewTerm.App", "bin", configuration, "net10.0", "crewterm.exe");
    }

    public Task InitializeAsync()
    {
        Assert.True(File.Exists(executable), "Build the application before exercising its executable: " + executable);
        Directory.CreateDirectory(Project);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HelpVersionAndDoctorWorkWithoutStartingDaemon()
    {
        var help = await RunAsync("--help");
        AssertSuccess(help);
        Assert.Contains("crewterm new", help.Output);
        Assert.Contains("crewterm run", help.Output);

        var version = await RunAsync("--version");
        AssertSuccess(version);
        Assert.Matches(@"^CrewTerm \d+\.\d+\.\d+", version.Output);

        var doctor = await RunAsync("doctor");
        AssertSuccess(doctor);
        Assert.Contains(state, doctor.Output);
        Assert.Contains("Shell", doctor.Output);
        Assert.False(File.Exists(Path.Combine(state, "server.lock")));
    }

    [Fact]
    public async Task TerminalCanBeCreatedWrittenReadListedAndClosedAcrossCliProcesses()
    {
        var created = ReadResponse(await RunAsync("new", "--command", "cmd.exe /d /q", "--project", Project, "--name", "CLI shell"));
        Assert.False(string.IsNullOrWhiteSpace(created.SessionId));
        var id = created.SessionId!;
        var marker = "CLI_TERMINAL_" + Guid.NewGuid().ToString("N");

        ReadResponse(await RunAsync("send", id, "--text", "echo " + marker + "\r"));
        await WaitForScreenLineAsync(id, marker);

        var listed = ReadResponse(await RunAsync("sessions"));
        var session = Assert.Single(listed.Sessions!, item => item.Id == id);
        Assert.Equal("CLI shell", session.Name);
        Assert.Equal(Project, session.WorkingDirectory);
        Assert.False(session.HasExited);

        ReadResponse(await RunAsync("close", id));
        Assert.DoesNotContain(ReadResponse(await RunAsync("sessions")).Sessions!, item => item.Id == id);
    }

    [Fact]
    public async Task BackgroundCommandContinuesAfterCliExitAndExposesActualOutput()
    {
        var marker = "CLI_TASK_" + Guid.NewGuid().ToString("N");
        var command = "echo " + marker + " & ping -n 2 127.0.0.1 >nul";
        var started = ReadResponse(await RunAsync("run", "--command", command, "--project", Project, "--name", "CLI check"));
        Assert.False(string.IsNullOrWhiteSpace(started.SessionId));
        var id = started.SessionId!;

        var listed = ReadResponse(await RunAsync("tasks"));
        var summary = Assert.Single(listed.Tasks!, item => item.Id == id);
        Assert.Equal("", summary.Output);

        var finished = await WaitForTaskAsync(id);
        Assert.Equal(0, finished.ExitCode);
        Assert.False(finished.Cancelled);
        Assert.Null(finished.Error);
        Assert.Equal("CLI check", finished.Name);
        Assert.Equal(Project, finished.WorkingDirectory);
        Assert.Contains(marker, finished.Output);
    }

    [Fact]
    public async Task FailedBackgroundCommandKeepsItsExitCodeSeparateFromCliRequestSuccess()
    {
        var started = ReadResponse(await RunAsync("run", "--command", "exit /b 7", "--project", Project));
        var finished = await WaitForTaskAsync(started.SessionId!);
        Assert.Equal(7, finished.ExitCode);
        Assert.False(finished.Cancelled);
    }

    [Theory]
    [InlineData("does-not-exist", "Unknown command")]
    [InlineData("new", "Required: --command")]
    public async Task InvalidCliCommandReturnsExitOneAndReadableError(string command, string error)
    {
        var result = await RunAsync(command);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(error, result.Error);
        Assert.Equal("", result.Output);
    }

    private async Task WaitForScreenLineAsync(string id, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            var result = await RunAsync("read", id);
            AssertSuccess(result);
            last = result.Output;
            if (last.Replace("\r", "").Split('\n').Any(line => line.Trim() == expected)) return;
            await Task.Delay(75);
        }
        Assert.Fail($"Terminal did not produce the expected complete line {expected}:\n{last}");
    }

    private async Task<DaemonTaskInfo> WaitForTaskAsync(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        DaemonTaskInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadResponse(await RunAsync("task", id)).Task;
            Assert.NotNull(last);
            if (last.Completed) return last;
            await Task.Delay(75);
        }
        throw new TimeoutException($"Background task did not finish: {last}");
    }

    private async Task<CliResult> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--state-dir");
        start.ArgumentList.Add(state);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start CLI");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(25));
            return new(process.ExitCode, await output, await error);
        }
        catch (TimeoutException)
        {
            var exited = process.HasExited;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            throw new TimeoutException($"CLI {arguments[0]} exceeded 25 seconds. Process exited: {exited}; stdout finished: {output.IsCompleted}; stderr finished: {error.IsCompleted}.");
        }
    }

    private static DaemonResponse ReadResponse(CliResult result)
    {
        AssertSuccess(result);
        var response = JsonSerializer.Deserialize<DaemonResponse>(result.Output);
        Assert.NotNull(response);
        Assert.True(response.Ok, response.Error);
        return response;
    }

    private static void AssertSuccess(CliResult result)
    {
        Assert.True(result.ExitCode == 0, $"CLI exited {result.ExitCode}: {result.Error}\n{result.Output}");
        Assert.Equal("", result.Error);
    }

    public async Task DisposeAsync()
    {
        var lockPath = Path.Combine(state, "server.lock");
        if (File.Exists(lockPath))
        {
            try { AssertSuccess(await RunAsync("stop")); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            {
                try { await new DaemonClient(state).CallAsync(new("stop")); }
                catch (Exception stopError) when (stopError is IOException or TimeoutException or OperationCanceledException) { }
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    using var released = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
            }
        }
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CrewTermCliTests")) + Path.DirectorySeparatorChar;
        var fullState = Path.GetFullPath(state);
        Assert.StartsWith(allowedRoot, fullState, StringComparison.OrdinalIgnoreCase);
        var cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(fullState))
        {
            try { Directory.Delete(fullState, recursive: true); }
            catch (IOException) when (DateTime.UtcNow < cleanupDeadline) { await Task.Delay(50); }
        }
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
