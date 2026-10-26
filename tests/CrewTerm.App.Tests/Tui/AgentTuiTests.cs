using CrewTerm.App.Client;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Automation.Activity;
using CrewTerm.Terminal.Pty;
using static CrewTerm.App.Tests.Tui.LayoutTuiTests;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// Agent 协作界面的端到端测试：在伪终端中启动真实的 <c>crewterm</c> 工作区，连接到独立临时状态目录中的真实 daemon，
/// 通过写入按键序列驱动 TUI，并用 <see cref="DaemonClient"/> 创建会话、上报状态和核对 daemon 中的消息与通知。
/// </summary>
[Collection(TuiCollection.Name)]
public sealed class AgentTuiTests
{
    /// <summary>
    /// 验证协作菜单：<c>Agent: Send message</c> 从当前选中的 Sender 向 Receiver 投递一条未确认消息；对方回复后，<c>Agent: Inbox</c> 能打开并显示回复，按 Esc 关闭时将其标记为已确认，
    /// 再次打开这条已确认的消息只显示全文，不会因重复确认而报错；
    /// <c>Agent: Handoff prompt</c> 把提示词作为真实输入写入接收方终端；<c>Agent: Wait for selected state</c> 在后台等待，外部上报 <c>done</c> 后显示等待结果和原因。
    /// </summary>
    [Fact]
    public async Task CooperationMenusDeliverAndReadDirectedMessages()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermAgentUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("agent-mail-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var a = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Sender"));
            await Wait(() => ui.Screen.GetText().Contains("┤Sender"), ui, "sender selected");
            var b = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Receiver"));
            await Wait(() => Sidebar(ui).Contains("Receiver"), ui, "receiver available");
            await Command(ui, "Agent: Send message");
            await Wait(() => ui.Screen.GetText().Contains("Choose recipient"), ui, "recipient selection");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Message text"), ui, "message prompt");
            ui.Write("Please review parser\r");
            await Wait(() => ui.Screen.GetText().Contains("Message queued"), ui, "message queued");
            var message = Assert.Single((await client.CallAsync(new("message-list", b.SessionId))).Messages!);
            Assert.Equal(a.SessionId, message.FromSessionId); Assert.Equal("Please review parser", message.Text);
            Assert.False(message.Acknowledged);
            var reply = await client.CallAsync(new("message-send", FromSessionId: b.SessionId, TargetSessionId: a.SessionId,
                Text: "Parser review ready", InReplyTo: message.Id));
            Assert.True(reply.Ok, reply.Error);
            await Command(ui, "Agent: Inbox");
            await Wait(() => ui.Screen.GetText().Contains("INBOX"), ui, "inbox list");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("INBOX MESSAGE") && ui.Screen.GetText().Contains("Parser review ready"), ui, "read reply");
            ui.Write("\u001b");
            await Wait(() => ui.Screen.GetText().Contains("Message marked read"), ui, "acknowledged reply");
            Assert.True(Assert.Single((await client.CallAsync(new("message-list", a.SessionId))).Messages!).Acknowledged);
            await Command(ui, "Agent: Inbox");
            await Wait(() => ui.Screen.GetText().Contains("INBOX"), ui, "inbox list again");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("INBOX MESSAGE"), ui, "reread acknowledged reply");
            ui.Write("\u001b");
            await Wait(() => ui.Screen.GetText().Contains("Message already read"), ui, "rereading does not acknowledge twice");
            Assert.DoesNotContain("already acknowledged", ui.Screen.GetText());
            await Command(ui, "Agent: Handoff prompt");
            await Wait(() => ui.Screen.GetText().Contains("Choose recipient"), ui, "handoff recipient");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Prompt to submit"), ui, "handoff prompt");
            ui.Write("echo HANDOFF_REAL_INPUT\r");
            await WaitForText(client, b.SessionId!, "HANDOFF_REAL_INPUT", ui);
            await Command(ui, "Agent: Wait for selected state");
            await Wait(() => ui.Screen.GetText().Contains("WAIT FOR STATE"), ui, "state chooser");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Wait timeout in seconds"), ui, "timeout prompt");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Waiting for Sender"), ui, "background state wait");
            await client.CallAsync(new("agent-report", a.SessionId, State: AgentActivityState.Done, Reason: "Coordination complete"));
            await Wait(() => ui.Screen.GetText().Contains("WAIT RESULT") && ui.Screen.GetText().Contains("Coordination complete"), ui, "wait completed");
            ui.Write("\u001b"); await Task.Delay(200);
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：用 Alt+5 聚焦侧栏并按下方向键选中第二个 Agent 后，该 Agent 上报 <c>Blocked</c> 使侧栏按优先级重排（出现 <c>[WAIT]</c>），
    /// 此时按 Enter 打开的仍是原先选中的会话，随后的键入落到第二个 Agent 的终端中，而不是落到重排后占据原位置的会话。
    /// </summary>
    [Fact]
    public async Task PriorityRefreshPreservesTheSelectedSessionIdentity()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermAgentUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("agent-selection-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "First Agent"));
            var b = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Second Agent"));
            await Wait(() => Sidebar(ui).Contains("Second Agent"), ui, "sidebar loaded");
            ui.Write("\u001b5"); await Task.Delay(200); ui.Write("\u001b[B"); await Task.Delay(150);
            await client.CallAsync(new("agent-report", b.SessionId, State: AgentActivityState.Blocked, Source: "api", Reason: "Needs input"));
            await Wait(() => Sidebar(ui).Contains("blocked ·"), ui, "priority reordered");
            ui.Write("\r"); await Task.Delay(200); ui.Write("echo STILL_SECOND\r");
            await WaitForText(client, b.SessionId!, "STILL_SECOND", ui);
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：A 上报 <c>Done</c>、B 上报 <c>Blocked</c> 后侧栏同时显示 <c>[WAIT]</c> 与 <c>[DONE*]</c>，且 <c>[WAIT]</c> 排在前面；
    /// 在 <c>Agent: Notifications</c> 列表中筛选并打开 Review B 只把 B 的通知标为已读，A 的通知仍未读；再次打开这条已读通知只切换会话、不会报错；
    /// B 再次上报完成后界面显示新通知，<c>Agent: Mark selected as read</c> 使 B 的状态变为 <see cref="AgentActivityState.Idle"/>。
    /// </summary>
    [Fact]
    public async Task NotificationsPrioritizeBlockedAndAcknowledgeOnlySelectedAgent()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermAgentUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("agent-state-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var a = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Build A"));
            var b = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Review B"));
            Assert.True((await client.CallAsync(new("agent-report", a.SessionId, State: AgentActivityState.Done, Source: "api", Reason: "Build finished"))).Ok);
            Assert.True((await client.CallAsync(new("agent-report", b.SessionId, State: AgentActivityState.Blocked, Source: "api", Reason: "Approve review"))).Ok);
            await Wait(() => Sidebar(ui).Contains("blocked ·") && Sidebar(ui).Contains("done ·"), ui, "semantic sidebar states");
            Assert.True(Sidebar(ui).IndexOf("blocked ·", StringComparison.Ordinal) < Sidebar(ui).IndexOf("done ·", StringComparison.Ordinal));
            await Command(ui, "Agent: Notifications");
            await Wait(() => ui.Screen.GetText().Contains("NOTIFICATIONS"), ui, "notification list");
            ui.Write("Review B"); await Task.Delay(200); ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Selected Review B"), ui, "notification opens session");
            var notices = (await client.CallAsync(new("notifications"))).Notifications!;
            Assert.All(notices.Where(n => n.SessionId == b.SessionId), n => Assert.True(n.Read));
            Assert.Contains(notices, n => n.SessionId == a.SessionId && !n.Read);
            await Command(ui, "Agent: Notifications");
            await Wait(() => ui.Screen.GetText().Contains("NOTIFICATIONS"), ui, "notification list again");
            ui.Write("Approve review"); await Task.Delay(200); ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("already read"), ui, "read notification reopens without error");
            Assert.DoesNotContain("Notification not found", ui.Screen.GetText());
            await client.CallAsync(new("agent-report", b.SessionId, State: AgentActivityState.Done, Source: "api", Reason: "Review finished"));
            await Wait(() => ui.Screen.GetText().Contains("Review finished"), ui, "new completion notification");
            await Command(ui, "Agent: Mark selected as read");
            await Wait(() => ui.Screen.GetText().Contains("Marked as read"), ui, "explicit seen state");
            Assert.Equal(AgentActivityState.Idle, (await client.CallAsync(new("agent-state", b.SessionId))).Activity!.State);
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：<c>Terminal: View / search scrollback</c> 打开的查看器中按 Ctrl+F 弹出查找框（标题 <c>Find in text</c>），
    /// 输入文本回车后查找框关闭、查看器仍然打开；Esc 关闭查看器后工作区可以正常分离。
    /// </summary>
    [Fact]
    public async Task ScrollbackViewerCtrlFOpensFind()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermAgentUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("scrollback-find-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var shell = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "History"));
            await Wait(() => ui.Screen.GetText().Contains("┤History"), ui, "history session selected");
            await client.CallAsync(new("input", shell.SessionId, Text: "echo SCROLL_MARKER\r"));
            await WaitForText(client, shell.SessionId!, "SCROLL_MARKER", ui);
            await Command(ui, "Terminal: View / search scrollback");
            await Wait(() => ui.Screen.GetText().Contains("SCROLLBACK"), ui, "scrollback viewer");
            ui.Write("\u0006");
            await Wait(() => ui.Screen.GetText().Contains("Find in text"), ui, "Ctrl+F find prompt");
            ui.Write("SCROLL_MARKER\r");
            await Wait(() => !ui.Screen.GetText().Contains("Find in text") && ui.Screen.GetText().Contains("SCROLLBACK"), ui, "find closes back to the viewer");
            ui.Write("\u001b");
            await Wait(() => !ui.Screen.GetText().Contains("SCROLLBACK"), ui, "viewer closed");
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 截取界面屏幕每一行的前 36 列（窗口边框与左侧 34 列的 spaces / agents 侧栏），用于只针对侧栏内容做断言。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <returns>按行以换行符连接的侧栏文本。</returns>
    private static string Sidebar(ITerminalSession ui) => string.Join('\n', ui.Screen.GetText().Split('\n').Select(line => line[..Math.Min(36, line.Length)]));
}
