using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// daemon Agent 协议的集成测试：在测试进程内运行真实的 <see cref="DaemonHost"/>（通过以状态目录区分的命名管道通信），
/// 用 <see cref="DaemonClient"/> 发送请求，验证 Agent 语义状态的推断与上报、通知及其确认、提示词定向投递、状态等待、消息邮箱与交接，以及 daemon 重启后的持久化。
/// </summary>
public sealed class DaemonAgentProtocolTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermAgentProtocolTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermAgentProtocolTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于停止当前 daemon 服务循环的取消源；<see cref="RestartAsync"/> 会替换为新实例。
    /// </summary>
    private CancellationTokenSource stop = new();

    /// <summary>
    /// 当前运行的 daemon 宿主。
    /// </summary>
    private DaemonHost? host;

    /// <summary>
    /// <see cref="DaemonHost.RunAsync"/> 返回的服务循环任务。
    /// </summary>
    private Task? server;

    /// <summary>
    /// 每次访问都新建一个连接到测试 daemon 的客户端。
    /// </summary>
    private DaemonClient Client => new(state);

    /// <summary>
    /// 创建 daemon 宿主并在后台运行其服务循环，再用 <c>ping</c> 确认 daemon 已能响应。
    /// </summary>
    /// <returns>表示启动过程的任务。</returns>
    public async Task InitializeAsync()
    {
        host = new DaemonHost(state);
        server = host.RunAsync(stop.Token);
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 验证：会话名含 <c>ESC</c> 控制序列时 <c>create</c> 被拒绝；消息正文含 <c>ESC</c> 时 <c>message-send</c> 被拒绝而换行与制表符允许；
    /// 工作目录带末尾分隔符时被规范化为不带分隔符的路径；<c>screen</c> 带 <c>IncludeText: false</c> 时只返回快照、不返回整屏文本。
    /// </summary>
    [Fact]
    public async Task DisplayedTextRejectsControlCharactersAndScreenTextIsOptional()
    {
        var rejected = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state, Name: "evil\u001b]0;title\u0007"));
        Assert.False(rejected.Ok);
        Assert.Contains("control characters", rejected.Error);
        var first = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state + Path.DirectorySeparatorChar, Name: "A"));
        var second = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state, Name: "B"));
        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        var escaped = await Client.CallAsync(new("message-send", FromSessionId: first.SessionId, TargetSessionId: second.SessionId, Text: "hi\u001b[2J"));
        Assert.False(escaped.Ok);
        var plain = await Client.CallAsync(new("message-send", FromSessionId: first.SessionId, TargetSessionId: second.SessionId, Text: "line 1\n\tline 2"));
        Assert.True(plain.Ok, plain.Error);
        var compact = await Client.CallAsync(new("screen", first.SessionId, IncludeText: false));
        Assert.True(compact.Ok, compact.Error);
        Assert.NotNull(compact.Screen);
        Assert.Null(compact.Text);
        Assert.NotNull((await Client.CallAsync(new("screen", first.SessionId))).Text);
        var expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state));
        Assert.All((await Client.CallAsync(new("list"))).Sessions!, session => Assert.Equal(expectedDirectory, session.WorkingDirectory));
    }

    /// <summary>
    /// 验证：相对工作目录被 daemon 拒绝（否则会按 daemon 的当前目录即安装目录解析）；提示词中的 <c>^C</c> 等控制字符被拒绝；
    /// 目标未启用 bracketed paste 时多行提示词被拒绝，避免每一行都被当作一次 Enter 提交。
    /// </summary>
    [Fact]
    public async Task RelativePathsAndUnsafePromptsAreRejected()
    {
        var relative = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: "."));
        Assert.False(relative.Ok);
        Assert.Contains("absolute path", relative.Error);
        Assert.False((await Client.CallAsync(new("task-start", Command: "echo x", WorkingDirectory: "relative"))).Ok);
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        var interrupt = await Client.CallAsync(new("agent-prompt", created.SessionId, Text: "stop\u0003"));
        Assert.False(interrupt.Ok);
        Assert.Contains("control characters", interrupt.Error);
        var multiline = await Client.CallAsync(new("agent-prompt", created.SessionId, Text: "first\nsecond"));
        Assert.False(multiline.Ok);
        Assert.Contains("bracketed paste", multiline.Error);
    }

    /// <summary>
    /// 验证：新建且尚未收到任何上报的存活会话，<c>agent-state</c> 响应序列化后的 <c>Activity.State</c> 为字符串 <c>Unknown</c>，并带有非空的原因说明。
    /// </summary>
    [Fact]
    public async Task FreshLiveSessionHasUnknownSemanticStateWithReason()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        var response = await Client.CallAsync(new("agent-state", SessionId: created.SessionId));
        Assert.True(response.Ok, response.Error);
        var activity = JsonSerializer.SerializeToElement(response).GetProperty("Activity");
        Assert.Equal("Unknown", activity.GetProperty("State").GetString());
        Assert.NotEmpty(activity.GetProperty("Reason").GetString()!);
    }

    /// <summary>
    /// 验证：连续两次相同的 <c>done</c> 上报只产生一条未读通知，会话未读计数为 1；<c>notification-read</c> 后 Agent 回到 <see cref="AgentActivityState.Idle"/>，
    /// 且该通知的已读状态在 daemon 重启后仍然保留。
    /// </summary>
    [Fact]
    public async Task ExplicitReportNotifiesOnceAndAcknowledgementIsPersistent()
    {
        var id = await CreateAsync();
        var report = new DaemonRequest("agent-report", id, State: AgentActivityState.Done, Source: "hook", Reason: "Turn finished");
        Assert.True((await Client.CallAsync(report)).Ok);
        Assert.True((await Client.CallAsync(report)).Ok);
        var notifications = await Client.CallAsync(new("notifications", id));
        Assert.True(notifications.Ok, notifications.Error);
        var notification = Assert.Single(notifications.Notifications!);
        Assert.False(notification.Read);
        Assert.Equal(1, Assert.Single((await Client.CallAsync(new("list"))).Sessions!).UnreadCount);
        Assert.True((await Client.CallAsync(new("notification-read", NotificationId: notification.Id))).Ok);
        Assert.Equal(AgentActivityState.Idle, (await Client.CallAsync(new("agent-state", id))).Activity!.State);
        await RestartAsync();
        Assert.True(Assert.Single((await Client.CallAsync(new("notifications", id))).Notifications!).Read);
    }

    /// <summary>
    /// 验证：<c>agent-prompt</c> 只把提示词写入指定会话（另一会话屏幕中没有标记）并把其状态置为 <see cref="AgentActivityState.Working"/>；
    /// Agent 处于 <see cref="AgentActivityState.Blocked"/> 时拒绝发送提示词（不会代替用户批准），目标会话不存在时同样失败。
    /// </summary>
    [Fact]
    public async Task PromptTargetsOneLiveTerminalAndNeverApprovesBlockedAgent()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        var prompted = await Client.CallAsync(new("agent-prompt", first, Text: "echo DIRECTED_PROMPT_MARKER"));
        Assert.True(prompted.Ok, prompted.Error);
        Assert.True(prompted.Delivered);
        await WaitForLineAsync(first, "DIRECTED_PROMPT_MARKER");
        Assert.DoesNotContain("DIRECTED_PROMPT_MARKER", (await Client.CallAsync(new("screen", second))).Text);
        Assert.Equal(AgentActivityState.Working, (await Client.CallAsync(new("agent-state", first))).Activity!.State);
        await Client.CallAsync(new("agent-report", first, State: AgentActivityState.Blocked, Reason: "Approval needed"));
        Assert.False((await Client.CallAsync(new("agent-prompt", first, Text: "yes"))).Ok);
        Assert.False((await Client.CallAsync(new("agent-prompt", "missing", Text: "echo WRONG_TARGET"))).Ok);
    }

    /// <summary>
    /// 验证 <c>agent-wait</c> 的三种结局：等待期间收到 <c>done</c> 上报时成功返回 <see cref="AgentActivityState.Done"/>；
    /// 等不到目标状态时超时失败并报告 <c>Timed out</c>；客户端取消令牌时调用抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    [Fact]
    public async Task WaitCanObserveReportTimeoutAndClientCancellation()
    {
        var id = await CreateAsync();
        var pending = Client.CallAsync(new("agent-wait", id, State: AgentActivityState.Done, TimeoutMilliseconds: 2000));
        Assert.True((await Client.CallAsync(new("agent-report", id, State: AgentActivityState.Done, Reason: "Finished"))).Ok);
        var observed = await pending;
        Assert.True(observed.Ok, observed.Error);
        Assert.Equal(AgentActivityState.Done, observed.Activity!.State);
        var timedOut = await Client.CallAsync(new("agent-wait", id, State: AgentActivityState.Blocked, TimeoutMilliseconds: 60));
        Assert.False(timedOut.Ok);
        Assert.Contains("Timed out", timedOut.Error);
        using var cancel = new CancellationTokenSource();
        var cancelled = Client.CallAsync(new("agent-wait", id, State: AgentActivityState.Working, TimeoutMilliseconds: 2000), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    /// <summary>
    /// 验证：16 个并发挂起的 <c>agent-wait</c> 不会占满 daemon 的控制连接容量，普通的 <c>agent-report</c> 仍能在 2 秒内得到处理；
    /// 之后所有等待在 4 秒内结束，至少一个观察到 <see cref="AgentActivityState.Done"/>，被拒绝的等待其错误信息中都提到 <c>wait</c>。
    /// </summary>
    [Fact]
    public async Task WaitingClientsCannotExhaustControlConnectionCapacity()
    {
        var id = await CreateAsync();
        var waits = Enumerable.Range(0, 16).Select(_ => Client.CallAsync(new("agent-wait", id,
            State: AgentActivityState.Done, TimeoutMilliseconds: 3000))).ToArray();
        await Task.Delay(150);
        var report = await Client.CallAsync(new("agent-report", id, State: AgentActivityState.Done, Reason: "Unblock waits"))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(report.Ok, report.Error);
        var results = await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Contains(results, response => response.Ok && response.Activity?.State == AgentActivityState.Done);
        Assert.All(results.Where(response => !response.Ok), response => Assert.Contains("wait", response.Error!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 验证：<c>message-send</c> 只把消息存入接收方邮箱（不写入终端，发送方邮箱为空），消息在 daemon 重启后仍以同一标识保留；
    /// 发送方确认该消息失败，只有接收方能确认，确认后标记为已确认。
    /// </summary>
    [Fact]
    public async Task MailboxIsDirectedAndPersistsAcrossDaemonRestart()
    {
        var sender = await CreateAsync();
        var receiver = await CreateAsync();
        var sent = await Client.CallAsync(new("message-send", FromSessionId: sender, TargetSessionId: receiver, Text: "Review file.cs"));
        Assert.True(sent.Ok, sent.Error);
        Assert.False(sent.Delivered);
        Assert.Empty((await Client.CallAsync(new("message-list", sender))).Messages!);
        Assert.False(Assert.Single((await Client.CallAsync(new("message-list", receiver))).Messages!).Acknowledged);
        await RestartAsync();
        var message = Assert.Single((await Client.CallAsync(new("message-list", receiver))).Messages!);
        Assert.Equal(sent.Message!.Id, message.Id);
        Assert.False((await Client.CallAsync(new("message-ack", sender, MessageId: message.Id))).Ok);
        Assert.True((await Client.CallAsync(new("message-ack", receiver, MessageId: message.Id))).Ok);
        Assert.True(Assert.Single((await Client.CallAsync(new("message-list", receiver))).Messages!).Acknowledged);
    }

    /// <summary>
    /// 验证：<c>agent-handoff</c> 记录一条发往显式目标的消息并把文本写入目标终端（<c>Delivered</c> 为 true，目标屏幕出现标记行），发送方终端中没有该标记。
    /// </summary>
    [Fact]
    public async Task HandoffStoresMessageAndDeliversOnlyToExplicitTarget()
    {
        var sender = await CreateAsync();
        var receiver = await CreateAsync();
        var result = await Client.CallAsync(new("agent-handoff", FromSessionId: sender, TargetSessionId: receiver,
            Text: "echo HANDOFF_MARKER"));
        Assert.True(result.Ok, result.Error);
        Assert.True(result.Delivered);
        Assert.Equal(receiver, result.Message!.ToSessionId);
        await WaitForLineAsync(receiver, "HANDOFF_MARKER");
        Assert.DoesNotContain("HANDOFF_MARKER", (await Client.CallAsync(new("screen", sender))).Text);
    }

    /// <summary>
    /// 验证：会话进程自行退出后 Agent 状态变为 <see cref="AgentActivityState.Exited"/>，但不产生完成通知；之后再向它发送提示词会失败。
    /// </summary>
    [Fact]
    public async Task ProcessExitBecomesExitedWithoutCompletionNotification()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Exit(0), WorkingDirectory: state));
        var result = await Client.CallAsync(new("agent-wait", created.SessionId, State: AgentActivityState.Exited, TimeoutMilliseconds: 15000));
        Assert.True(result.Ok, result.Error);
        Assert.Equal(AgentActivityState.Exited, result.Activity!.State);
        Assert.Empty((await Client.CallAsync(new("notifications", created.SessionId))).Notifications!);
        Assert.False((await Client.CallAsync(new("agent-prompt", created.SessionId, Text: "echo too-late"))).Ok);
    }

    /// <summary>
    /// 验证：对声明了已知提供方（<c>codex</c>）的会话，经 <c>input</c> 提交以回车结尾的原始输入会把状态置为 <see cref="AgentActivityState.Working"/>（来源 <c>input</c>，原因提到 submitted）；
    /// 屏幕出现 <c>Do you want to proceed?</c> 这类批准提示时，状态经屏幕检测转为 <see cref="AgentActivityState.Blocked"/>（来源 <c>screen</c>）；之后钩子上报 <c>done</c> 仍能把状态设为 Done。
    /// </summary>
    [Fact]
    public async Task RawInputSubmissionHasEvidenceAndApprovalFallbackForKnownProvider()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state, Provider: "codex"));
        var id = created.SessionId!;
        await Client.CallAsync(new("agent-report", id, State: AgentActivityState.Done, Source: "hook", Reason: "Previous turn ended"));
        Assert.True((await Client.CallAsync(new("input", id, Text: "echo INPUT_ACTIVITY_MARKER\r"))).Ok);
        var working = (await Client.CallAsync(new("agent-state", id))).Activity!;
        Assert.Equal(AgentActivityState.Working, working.State);
        Assert.Equal("input", working.Source);
        Assert.Contains("submitted", working.Reason, StringComparison.OrdinalIgnoreCase);
        await Client.CallAsync(new("input", id, Text: "echo Do you want to proceed?\r"));
        var blocked = await Client.CallAsync(new("agent-wait", id, State: AgentActivityState.Blocked, TimeoutMilliseconds: 15000));
        Assert.True(blocked.Ok, blocked.Error);
        Assert.Equal("screen", blocked.Activity!.Source);
        await Client.CallAsync(new("agent-report", id, State: AgentActivityState.Done, Source: "hook", Reason: "Provider turn completed"));
        Assert.Equal(AgentActivityState.Done, (await Client.CallAsync(new("agent-state", id))).Activity!.State);
    }

    /// <summary>
    /// 验证：未声明提供方的普通 Shell 会话收到原始输入并执行后，Agent 状态仍为 <see cref="AgentActivityState.Unknown"/>，不会被误判为正在工作。
    /// </summary>
    [Fact]
    public async Task RawShellInputDoesNotPretendAnAgentIsWorking()
    {
        var id = await CreateAsync();
        await Client.CallAsync(new("input", id, Text: "echo SHELL_INPUT_MARKER\r"));
        await WaitForLineAsync(id, "SHELL_INPUT_MARKER");
        Assert.Equal(AgentActivityState.Unknown, (await Client.CallAsync(new("agent-state", id))).Activity!.State);
    }

    /// <summary>
    /// 验证：经 <c>agent-prompt</c> 提交提示词后，屏幕随后出现批准提示时，状态仍能由屏幕检测转为 <see cref="AgentActivityState.Blocked"/>（来源 <c>screen</c>）。
    /// </summary>
    [Fact]
    public async Task PromptSubmissionStillAllowsLaterScreenApprovalToBecomeBlocked()
    {
        var id = await CreateAsync();
        Assert.True((await Client.CallAsync(new("agent-prompt", id, Text: "echo Do you want to proceed?"))).Ok);
        var blocked = await Client.CallAsync(new("agent-wait", id, State: AgentActivityState.Blocked, TimeoutMilliseconds: 15000));
        Assert.True(blocked.Ok, blocked.Error);
        Assert.Equal("screen", blocked.Activity!.Source);
    }

    /// <summary>
    /// 在状态目录中创建一个运行交互式 Shell（<see cref="TestShell.Interactive"/>）的会话并断言成功。
    /// </summary>
    /// <returns>新会话的标识。</returns>
    private async Task<string> CreateAsync()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        return created.SessionId!;
    }

    /// <summary>
    /// 在 5 秒内每 40 毫秒读取一次会话屏幕，直到某一行去掉首尾空白后恰好等于期望文本（排除只出现在回显命令行中的情况）；超时则使测试失败。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="line">期望出现的完整行。</param>
    /// <returns>表示等待过程的任务。</returns>
    private async Task WaitForLineAsync(string id, string line)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var screen = (await Client.CallAsync(new("screen", id))).Text!;
            if (screen.Replace("\r", "").Split('\n').Any(item => item.Trim() == line)) return;
            await Task.Delay(40);
        }
        Assert.Fail("Missing terminal output: " + line);
    }

    /// <summary>
    /// 模拟 daemon 重启：取消并等待当前服务循环结束，释放宿主与取消源，再用同一状态目录重新创建并启动 daemon。
    /// </summary>
    /// <returns>表示重启过程的任务。</returns>
    private async Task RestartAsync()
    {
        await stop.CancelAsync();
        await server!;
        host!.Dispose();
        stop.Dispose();
        stop = new();
        await InitializeAsync();
    }

    /// <summary>
    /// 停止 daemon 服务循环并等待其结束，释放宿主与取消源，最后删除状态目录。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (server is not null) await server;
        host?.Dispose();
        stop.Dispose();
        await TestStateDirectory.DeleteAsync(state);
    }
}
