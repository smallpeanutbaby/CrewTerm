using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.Automation.Tasks;
using CrewTerm.Core.Settings;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// 后台任务协议的 daemon 集成测试：先把工作区设置中的最大并发任务数写为 1，再在测试进程内运行真实的 <see cref="DaemonHost"/>，
/// 覆盖任务启动、新客户端重连查询与精简列表、流水线遇错即停、取消、并发上限、<c>stop</c> 时取消任务、非法输入的拒绝，以及释放宿主时排空空闲连接。
/// </summary>
public sealed class DaemonTaskProtocolTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermTaskProtocolTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermTaskProtocolTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于停止 daemon 服务循环的取消源。
    /// </summary>
    private readonly CancellationTokenSource stop = new();

    /// <summary>
    /// 当前运行的 daemon 宿主。
    /// </summary>
    private DaemonHost? host;

    /// <summary>
    /// <see cref="DaemonHost.RunAsync"/> 返回的服务循环任务。
    /// </summary>
    private Task? server;

    /// <summary>
    /// 创建状态目录并把 <c>MaximumConcurrentTasks</c> 设为 1 后启动 daemon，再最多尝试 40 次 <c>ping</c>（超时后等待 25 毫秒重试）确认其已就绪。
    /// </summary>
    /// <returns>表示启动过程的任务。</returns>
    /// <exception cref="TimeoutException">40 次尝试都未得到响应时抛出。</exception>
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

    /// <summary>
    /// 验证：<c>task-start</c> 成功并在响应中返回后台任务标识。
    /// </summary>
    [Fact]
    public async Task TaskStartReturnsBackgroundTaskId()
    {
        var response = await new DaemonClient(state).CallAsync(new("task-start", Name: "test", Command: "echo actual-task", WorkingDirectory: state));
        Assert.True(response.Ok, response.Error);
        Assert.NotNull(response.SessionId);
    }

    /// <summary>
    /// 验证：另一个新建的客户端能用 <c>task-get</c> 看到运行中任务已产生的输出；<c>task-list</c> 返回的摘要不含输出；
    /// 任务完成后退出码为 0，输出中的中文文本完整保留。
    /// </summary>
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

    /// <summary>
    /// 验证：<c>pipeline-start</c> 的第一步以退出码 7 失败后流水线立即停止：整体退出码为 7，输出含第一步的内容，第二步没有执行（未生成 <c>marker</c> 文件）。
    /// </summary>
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

    /// <summary>
    /// 验证：对已开始输出的长时间任务执行 <c>task-cancel</c> 后，任务结束并标记为已取消。
    /// </summary>
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

    /// <summary>
    /// 验证：daemon 读取并遵守设置中的最大并发任务数 1：第一个任务运行期间启动的第二个任务会排队，
    /// 因此第二个任务写入的文件修改时间不早于第一个任务结束前写入的文件。
    /// </summary>
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

    /// <summary>
    /// 验证：<c>stop</c> 请求在服务循环返回之前取消运行中的后台任务，任务历史中把该任务记录为已取消。
    /// </summary>
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

    /// <summary>
    /// 验证：查询或取消不存在的任务、启动没有步骤的流水线、在不存在的工作目录中启动任务都返回失败，且任务列表保持为空。
    /// </summary>
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

    /// <summary>
    /// 验证：空白命令和超过 8192 个字符的命令（无论作为单个任务还是流水线步骤）都在执行前被拒绝，任务列表保持为空。
    /// </summary>
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

    /// <summary>
    /// 验证：16 个客户端已连上 daemon 命名管道但都未发送请求时释放宿主，服务循环仍能在 10 秒内安全结束。
    /// </summary>
    [Fact]
    public async Task DisposeSafelyDrainsConnectionsWaitingForRequests()
    {
        var held = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeProtocol.PipeName(state),
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                held.Add(pipe);
                await pipe.ConnectAsync(2000);
            }
            host!.Dispose();
            await server!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { foreach (var pipe in held) pipe.Dispose(); }
    }

    /// <summary>
    /// 每 25 毫秒用 <c>task-get</c> 查询一次任务（断言请求成功且带有任务信息），直到条件成立；整个等待以 10 秒为限。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="id">任务标识。</param>
    /// <param name="condition">判断任务信息是否已满足期望的条件。</param>
    /// <returns>首个满足条件的任务信息。</returns>
    /// <exception cref="OperationCanceledException">10 秒内条件仍未成立时抛出。</exception>
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

    /// <summary>
    /// 停止 daemon 服务循环并等待其结束，释放宿主与取消源，最后递归删除状态目录。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (server is not null) await server;
        host?.Dispose();
        stop.Dispose();
        if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
    }
}
