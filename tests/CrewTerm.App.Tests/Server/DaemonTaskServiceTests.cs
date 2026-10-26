using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.Automation.Tasks;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// 直接实例化 <see cref="DaemonTaskService"/>（不经过命名管道）的测试：以真实 Shell 进程运行命令，验证任务在调用方不等待时继续运行、
/// 完成结果写入历史并可被新实例读回、取消、流水线遇错即停、停止时取消运行中与排队中的任务、输出快照的大小上限、缺失命令的退出码，以及已完成任务的保留数量上限。
/// </summary>
public sealed class DaemonTaskServiceTests : IDisposable
{
    /// <summary>
    /// 存放任务历史的临时状态目录（系统临时目录下的 <c>crewterm-daemon-tasks-&lt;GUID&gt;</c>），也是各任务命令的工作目录。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-daemon-tasks-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建临时状态目录。
    /// </summary>
    public DaemonTaskServiceTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：启动后无人等待的任务继续运行（已输出 <c>started</c> 但尚未完成，且出现在列表中），完成后退出码为 0 且输出含 <c>finished</c>；
    /// 用同一目录新建的服务实例能从历史中读回这条已完成任务及其输出。
    /// </summary>
    [Fact]
    public async Task TaskContinuesWithoutCallerAndCompletedRunReopensFromHistory()
    {
        using var service = new DaemonTaskService(root);
        var command = TestShell.Sequence("echo started", TestShell.Sleep(1), "echo finished");
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

    /// <summary>
    /// 验证：对运行中的任务调用 <c>Cancel</c> 返回 true，任务结束后标记为已取消；对已结束的任务再次调用返回 false。
    /// </summary>
    [Fact]
    public async Task CancelStopsAnActiveTaskAndRecordsCancellation()
    {
        using var service = new DaemonTaskService(root);
        var command = TestShell.Sequence("echo started", TestShell.Sleep(59));
        var id = service.Start(new("cancel", command, root));
        await WaitForAsync(service, id, info => info.Output.Contains("started"));
        Assert.True(service.Cancel(id));
        var stopped = await WaitForAsync(service, id, info => info.Completed);
        Assert.True(stopped.Cancelled);
        Assert.False(service.Cancel(id));
    }

    /// <summary>
    /// 验证：流水线第一步以退出码 7 失败时不再运行后续步骤（未生成 <c>marker</c> 文件），结果退出码为 7、未取消，名称为流水线名 <c>checks</c>。
    /// </summary>
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

    /// <summary>
    /// 验证：最大并发为 1 时，<c>StopAsync</c> 在 10 秒内同时取消运行中的任务和排队中的任务，排队任务的命令从未执行（未生成文件）；停止后再调用 <c>Start</c> 抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    [Fact]
    public async Task StopCancelsRunningAndQueuedTasksBeforeAnyQueuedSideEffect()
    {
        using var service = new DaemonTaskService(root, maximumConcurrency: 1);
        var command = TestShell.Sequence("echo started", TestShell.Sleep(59));
        var first = service.Start(new("first", command, root));
        await WaitForAsync(service, first, info => info.Output.Contains("started"));
        var second = service.Start(new("second", "echo bad > should-not-run", root));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.Get(first)!.Cancelled);
        Assert.True(service.Get(second)!.Cancelled);
        Assert.False(File.Exists(Path.Combine(root, "should-not-run")));
        Assert.Throws<InvalidOperationException>(() => service.Start(new("third", "echo bad", root)));
    }

    /// <summary>
    /// 验证：命令输出 5000 行长文本时，任务快照中的输出不超过 262144 个字符，并保留最新的内容（包含第 5000 行的编号）。
    /// </summary>
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

    /// <summary>
    /// 验证：命令不存在时任务正常结束为失败：退出码为 Shell 返回的非零值、未标记取消，输出中包含 Shell 的错误信息。
    /// </summary>
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

    /// <summary>
    /// 验证：保留数量设为 2 时，依次完成 3 个任务后最早的任务从内存中释放（<c>Get</c> 返回 null），服务列表和任务历史都只按从新到旧保留后两个任务。
    /// </summary>
    [Fact]
    public async Task CompletedTasksBeyondRetentionAreReleasedFromMemoryAndHistory()
    {
        using var service = new DaemonTaskService(root, retainedTasks: 2);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = service.Start(new($"quick {i}", "echo done", root));
            await WaitForAsync(service, id, info => info.Completed);
            ids.Add(id);
        }
        Assert.Null(service.Get(ids[0]));
        Assert.Equal(new[] { ids[2], ids[1] }, service.List().Select(info => info.Id));
        Assert.Equal(new[] { ids[2], ids[1] }, new TaskHistory(root).List().Select(result => result.Id));
    }

    /// <summary>
    /// 每 25 毫秒取一次任务快照（断言任务存在），直到条件成立；整个等待以 10 秒为限。
    /// </summary>
    /// <param name="service">被测任务服务。</param>
    /// <param name="id">任务标识。</param>
    /// <param name="condition">判断任务快照是否已满足期望的条件。</param>
    /// <returns>首个满足条件的任务快照。</returns>
    /// <exception cref="OperationCanceledException">10 秒内条件仍未成立时抛出。</exception>
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

    /// <summary>
    /// 递归删除临时状态目录。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
