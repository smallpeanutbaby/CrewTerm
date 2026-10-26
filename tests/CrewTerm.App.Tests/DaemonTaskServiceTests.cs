using CrewTerm.App.Server;
using CrewTerm.Automation;

namespace CrewTerm.App.Tests;

public sealed class DaemonTaskServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-daemon-tasks-" + Guid.NewGuid().ToString("N"));
    public DaemonTaskServiceTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task TaskContinuesWithoutCallerAndCompletedRunReopensFromHistory()
    {
        using var service = new DaemonTaskService(root);
        var command = OperatingSystem.IsWindows() ? "echo started & ping -n 2 127.0.0.1 >nul & echo finished" : "echo started; sleep 1; echo finished";
        var id = service.Start(new("persistent", command, root));
        var running = await WaitForAsync(service, id, info => info.Output.Contains("started"));
        Assert.False(running.Completed);
        Assert.Contains(service.List(), item => item.Id == id);
        var completed = await WaitForAsync(service, id, info => info.Completed);
        Assert.Equal(0, completed.ExitCode);
        Assert.Contains("finished", completed.Output);
        using var reconnected = new DaemonTaskService(root);
        var restored = reconnected.Get(id);
        Assert.NotNull(restored);
        Assert.True(restored.Completed);
        Assert.Contains("finished", restored.Output);
    }

    [Fact]
    public async Task CancelStopsAnActiveTaskAndRecordsCancellation()
    {
        using var service = new DaemonTaskService(root);
        var command = OperatingSystem.IsWindows() ? "echo started & ping -n 60 127.0.0.1 >nul" : "echo started; sleep 60";
        var id = service.Start(new("cancel", command, root));
        await WaitForAsync(service, id, info => info.Output.Contains("started"));
        Assert.True(service.Cancel(id));
        var stopped = await WaitForAsync(service, id, info => info.Completed);
        Assert.True(stopped.Cancelled);
        Assert.False(service.Cancel(id));
    }

    [Fact]
    public async Task FailedPipelineDoesNotRunLaterStep()
    {
        using var service = new DaemonTaskService(root);
        var exit = OperatingSystem.IsWindows() ? "exit /b 7" : "exit 7";
        var id = service.StartPipeline("checks", [new("fail", exit, root), new("never", "echo bad > marker", root)]);
        var result = await WaitForAsync(service, id, info => info.Completed);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.False(File.Exists(Path.Combine(root, "marker")));
        Assert.Equal("checks", result.Name);
    }

    [Fact]
    public async Task StopCancelsRunningAndQueuedTasksBeforeAnyQueuedSideEffect()
    {
        using var service = new DaemonTaskService(root, maximumConcurrency: 1);
        var command = OperatingSystem.IsWindows() ? "echo started & ping -n 60 127.0.0.1 >nul" : "echo started; sleep 60";
        var first = service.Start(new("first", command, root));
        await WaitForAsync(service, first, info => info.Output.Contains("started"));
        var second = service.Start(new("second", "echo bad > should-not-run", root));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.Get(first)!.Cancelled);
        Assert.True(service.Get(second)!.Cancelled);
        Assert.False(File.Exists(Path.Combine(root, "should-not-run")));
        Assert.Throws<InvalidOperationException>(() => service.Start(new("third", "echo bad", root)));
    }

    [Fact]
    public async Task SnapshotOutputIsBoundedAndRetainsLatestLines()
    {
        using var service = new DaemonTaskService(root);
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,5000) do @echo 012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789-%i"
            : "awk 'BEGIN { for(i=1;i<=5000;i++) print \"012345678901234567890123456789012345678901234567890123456789\",i }'";
        var id = service.Start(new("verbose", command, root));
        var result = await WaitForAsync(service, id, info => info.Completed);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Output.Length <= 262144);
        Assert.Contains("5000", result.Output);
    }

    [Fact]
    public async Task MissingCommandIsACompletedFailureWithRealExitCode()
    {
        using var service = new DaemonTaskService(root);
        var id = service.Start(new("missing", "crewterm-command-that-does-not-exist-128396", root));
        var result = await WaitForAsync(service, id, info => info.Completed);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.NotEmpty(result.Output);
    }

    private static async Task<DaemonTaskInfo> WaitForAsync(DaemonTaskService service, string id, Func<DaemonTaskInfo, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var info = service.Get(id);
            Assert.NotNull(info);
            if (condition(info)) return info;
            await Task.Delay(25, timeout.Token);
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
