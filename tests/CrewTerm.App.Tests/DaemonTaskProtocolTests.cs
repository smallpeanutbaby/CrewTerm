using CrewTerm.App.Server;
using CrewTerm.Automation;
using CrewTerm.Core;

namespace CrewTerm.App.Tests;

public sealed class DaemonTaskProtocolTests : IAsyncLifetime
{
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermTaskProtocolTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource stop = new();
    private DaemonHost? host;
    private Task? server;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(state);
        var settingsStore = new WorkspaceSettingsStore(state);
        var settings = settingsStore.Load();
        settings.MaximumConcurrentTasks = 1;
        settingsStore.Save(settings);
        host = new DaemonHost(state);
        server = host.RunAsync(stop.Token);
        var client = new DaemonClient(state);
        for (var i = 0; i < 40; i++)
        {
            try { await client.CallAsync(new("ping")); return; }
            catch (TimeoutException) { await Task.Delay(25); }
        }
        throw new TimeoutException("The task test daemon did not start.");
    }

    [Fact]
    public async Task TaskStartReturnsBackgroundTaskId()
    {
        var response = await new DaemonClient(state).CallAsync(new("task-start", Name: "test", Command: "echo actual-task", WorkingDirectory: state));
        Assert.True(response.Ok, response.Error);
        Assert.NotNull(response.SessionId);
    }

    [Fact]
    public async Task NewClientSeesRunningTaskAndListsOnlyCompactSummaries()
    {
        var command = "echo started & ping -n 2 127.0.0.1 >nul & echo 你好终端";
        var started = await new DaemonClient(state).CallAsync(new("task-start", Name: "reconnect", Command: command, WorkingDirectory: state));
        Assert.True(started.Ok, started.Error);
        var client = new DaemonClient(state);
        await WaitForTaskAsync(client, started.SessionId!, info => info.Output.Contains("started"));
        var listed = await client.CallAsync(new("task-list"));
        Assert.True(listed.Ok, listed.Error);
        var summary = Assert.Single(listed.Tasks!, info => info.Id == started.SessionId);
        Assert.Empty(summary.Output);
        var final = await WaitForTaskAsync(client, started.SessionId!, info => info.Completed);
        Assert.Equal(0, final.ExitCode);
        Assert.Contains("你好终端", final.Output);
    }

    [Fact]
    public async Task PipelineProtocolStopsAtFirstFailedStep()
    {
        var client = new DaemonClient(state);
        var response = await client.CallAsync(new("pipeline-start", Name: "checks", Steps:
        [new("fail", "echo failure & exit /b 7", state), new("never", "echo bad > marker", state)]));
        Assert.True(response.Ok, response.Error);
        var completed = await WaitForTaskAsync(client, response.SessionId!, info => info.Completed);
        Assert.Equal(7, completed.ExitCode);
        Assert.Contains("failure", completed.Output);
        Assert.False(File.Exists(Path.Combine(state, "marker")));
    }

    [Fact]
    public async Task TaskCancelProtocolCancelsActiveTask()
    {
        var client = new DaemonClient(state);
        var started = await client.CallAsync(new("task-start", Command: "echo started & ping -n 60 127.0.0.1 >nul", WorkingDirectory: state));
        Assert.True(started.Ok, started.Error);
        await WaitForTaskAsync(client, started.SessionId!, info => info.Output.Contains("started"));
        Assert.True((await client.CallAsync(new("task-cancel", SessionId: started.SessionId))).Ok);
        var result = await WaitForTaskAsync(client, started.SessionId!, info => info.Completed);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task DaemonReadsConfiguredParallelism()
    {
        var client = new DaemonClient(state);
        var first = await client.CallAsync(new("task-start", Command: "echo started & ping -n 3 127.0.0.1 >nul & echo done > first-finished", WorkingDirectory: state));
        Assert.True(first.Ok, first.Error);
        await WaitForTaskAsync(client, first.SessionId!, info => info.Output.Contains("started"));
        var second = await client.CallAsync(new("task-start", Command: "echo done > second-finished", WorkingDirectory: state));
        Assert.True(second.Ok, second.Error);
        await WaitForTaskAsync(client, first.SessionId!, info => info.Completed);
        await WaitForTaskAsync(client, second.SessionId!, info => info.Completed);
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(state, "first-finished")) <=
            File.GetLastWriteTimeUtc(Path.Combine(state, "second-finished")));
    }

    [Fact]
    public async Task StopProtocolCancelsBackgroundTasksBeforeServerReturns()
    {
        var client = new DaemonClient(state);
        var started = await client.CallAsync(new("task-start", Command: "echo started & ping -n 60 127.0.0.1 >nul", WorkingDirectory: state));
        Assert.True(started.Ok, started.Error);
        await WaitForTaskAsync(client, started.SessionId!, info => info.Output.Contains("started"));
        Assert.True((await client.CallAsync(new("stop"))).Ok);
        await server!.WaitAsync(TimeSpan.FromSeconds(10));
        var result = Assert.Single(new TaskHistory(state).List(), item => item.Id == started.SessionId);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task MissingTaskAndEmptyPipelineAreErrorsWithoutCreatingTasks()
    {
        var client = new DaemonClient(state);
        Assert.False((await client.CallAsync(new("task-get", SessionId: "missing"))).Ok);
        Assert.False((await client.CallAsync(new("task-cancel", SessionId: "missing"))).Ok);
        Assert.False((await client.CallAsync(new("pipeline-start", Steps: []))).Ok);
        Assert.False((await client.CallAsync(new("task-start", Command: "echo x", WorkingDirectory: Path.Combine(state, "missing")))).Ok);
        Assert.Empty((await client.CallAsync(new("task-list"))).Tasks!);
    }

    [Fact]
    public async Task EmptyAndOversizedCommandsAreRejectedBeforeExecution()
    {
        var client = new DaemonClient(state);
        Assert.False((await client.CallAsync(new("task-start", Command: " ", WorkingDirectory: state))).Ok);
        var oversized = new string('x', 8193);
        Assert.False((await client.CallAsync(new("task-start", Command: oversized, WorkingDirectory: state))).Ok);
        Assert.False((await client.CallAsync(new("pipeline-start", Steps: [new("bad", oversized, state)]))).Ok);
        Assert.Empty((await client.CallAsync(new("task-list"))).Tasks!);
    }

    [Fact]
    public async Task DisposeSafelyDrainsConnectionsWaitingForRequests()
    {
        var held = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", DaemonClient.PipeName(state),
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                held.Add(pipe);
                await pipe.ConnectAsync(2000);
            }
            host!.Dispose();
            await server!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { foreach (var pipe in held) pipe.Dispose(); }
    }

    private static async Task<DaemonTaskInfo> WaitForTaskAsync(DaemonClient client, string id, Func<DaemonTaskInfo, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var response = await client.CallAsync(new("task-get", SessionId: id), timeout.Token);
            Assert.True(response.Ok, response.Error);
            Assert.NotNull(response.Task);
            if (condition(response.Task)) return response.Task;
            await Task.Delay(25, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (server is not null) await server;
        host?.Dispose();
        stop.Dispose();
        if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
    }
}
