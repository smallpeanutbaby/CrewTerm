using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.App.Tests.Fixtures;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// daemon 基本会话协议的集成测试：在测试进程内运行真实的 <see cref="DaemonHost"/>，通过命名管道上的 <see cref="DaemonClient"/> 发送请求，
/// 覆盖跨客户端重连看到同一存活会话、无效工作目录、未知会话、非法尺寸、持久化失败，以及连接被占满后服务的可用性。
/// </summary>
public sealed class DaemonTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermDaemonTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermDaemonTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于停止 daemon 服务循环的取消源。
    /// </summary>
    private readonly CancellationTokenSource stop = new();

    /// <summary>
    /// <see cref="DaemonHost.RunAsync"/> 返回的服务循环任务。
    /// </summary>
    private Task? serverTask;

    /// <summary>
    /// 当前运行的 daemon 宿主。
    /// </summary>
    private DaemonHost? server;

    /// <summary>
    /// 创建 daemon 宿主并在后台运行其服务循环，再最多尝试 40 次 <c>ping</c>（超时后等待 25 毫秒重试）确认其已就绪。
    /// </summary>
    /// <returns>表示启动过程的任务。</returns>
    /// <exception cref="TimeoutException">40 次尝试都未得到响应时抛出。</exception>
    public async Task InitializeAsync()
    {
        server = new DaemonHost(state);
        serverTask = server.RunAsync(stop.Token);
        var client = new DaemonClient(state);
        for (var i = 0; i < 40; i++)
        {
            try { await client.CallAsync(new("ping")); return; }
            catch (TimeoutException) { await Task.Delay(25); }
        }
        throw new TimeoutException("Test daemon did not start");
    }

    /// <summary>
    /// 验证：一个客户端创建的会话能被另一个新客户端在列表中看到、写入输入并读到屏幕输出；由第二个客户端关闭后，第一个客户端看到的会话列表为空。
    /// </summary>
    [Fact]
    public async Task ReconnectingClientSeesSameLiveSession()
    {
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        var second = new DaemonClient(state);
        var sessions = await second.CallAsync(new("list"));
        Assert.Contains(sessions.Sessions!, s => s.Id == created.SessionId);
        await second.CallAsync(new("input", SessionId: created.SessionId, Text: "echo crewterm-reconnected\r\n"));
        for (var i = 0; i < 60; i++)
        {
            var screen = await second.CallAsync(new("screen", SessionId: created.SessionId));
            if (screen.Text?.Contains("crewterm-reconnected") == true) break;
            await Task.Delay(50);
        }
        var result = await second.CallAsync(new("screen", SessionId: created.SessionId));
        Assert.Contains("crewterm-reconnected", result.Text);
        Assert.True((await second.CallAsync(new("close", SessionId: created.SessionId))).Ok);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    /// <summary>
    /// 验证：工作目录不存在时 <c>create</c> 失败并给出非空的错误信息，不留下任何会话。
    /// </summary>
    [Fact]
    public async Task InvalidWorkingDirectoryFailsWithoutSession()
    {
        var client = new DaemonClient(state);
        var result = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: Path.Combine(state, "missing")));
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error!);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    /// <summary>
    /// 验证：向不存在的会话发送输入时请求失败且错误信息提到 <c>Session</c>，输入不会被转发到其他会话。
    /// </summary>
    [Fact]
    public async Task UnknownSessionDoesNotRouteInputElsewhere()
    {
        var result = await new DaemonClient(state).CallAsync(new("input", SessionId: "absent", Text: "exit\r"));
        Assert.False(result.Ok);
        Assert.Contains("Session", result.Error);
    }

    /// <summary>
    /// 验证：以负的列数调整会话尺寸的 <c>resize</c> 请求被拒绝。
    /// </summary>
    [Fact]
    public async Task InvalidResizeIsRejected()
    {
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        var result = await client.CallAsync(new("resize", SessionId: created.SessionId, Columns: -1));
        Assert.False(result.Ok);
    }

    /// <summary>
    /// 验证：在 <c>saved-sessions.json</c> 的位置预先建立同名目录使会话快照写入失败时，<c>create</c> 返回失败，且不会留下调用方不知道的存活会话。
    /// </summary>
    [Fact]
    public async Task PersistenceFailureDoesNotLeaveUnreportedSession()
    {
        Directory.CreateDirectory(Path.Combine(state, "saved-sessions.json"));
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.False(created.Ok);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    /// <summary>
    /// 验证：16 个客户端同时连上命名管道却不发送请求、随后全部断开后，daemon 服务循环没有终止，仍能响应 <c>ping</c>。
    /// </summary>
    [Fact]
    public async Task ConnectionSaturationDoesNotTerminateServer()
    {
        var held = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeProtocol.PipeName(state),
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                held.Add(pipe);
                await pipe.ConnectAsync(10000);
            }
            await Task.Delay(150);
        }
        finally { foreach (var pipe in held) pipe.Dispose(); }
        await Task.Delay(150);
        Assert.True((await new DaemonClient(state).CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 停止 daemon 服务循环并等待其结束，释放宿主与取消源，最后删除状态目录。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (serverTask is not null) await serverTask;
        server?.Dispose();
        stop.Dispose();
        await TestStateDirectory.DeleteAsync(state);
    }
}
