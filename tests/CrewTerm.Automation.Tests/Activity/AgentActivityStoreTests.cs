using CrewTerm.Automation.Activity;

namespace CrewTerm.Automation.Tests.Activity;

/// <summary>
/// 覆盖 <see cref="AgentActivityStore"/> 的语义状态迁移、通知与确认、屏幕提示识别、关注优先级、会话间邮箱、
/// 持久化上限、异步等待、进程退出、损坏文件保护以及忘记会话等行为。
/// 每个测试使用系统临时目录下独立的状态目录，读写真实的 <c>agent-activity.json</c>，并常通过重新构造实例验证持久化结果。
/// </summary>
public sealed class AgentActivityStoreTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的状态目录（以随机 GUID 命名）；不预先创建，由 <see cref="AgentActivityStore"/> 按需创建。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermActivityTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 验证：从工作中转为等待决定并重复上报同一事件时只生成一条未读通知；多次读取通知列表不会将其标记为已读；
    /// 显式确认后未读数归零，但会话仍保持 <see cref="AgentActivityState.Blocked"/>。
    /// </summary>
    [Fact]
    public void BlockedTransitionNotifiesOnceAndReadingDoesNotAcknowledge()
    {
        var store = new AgentActivityStore(root);
        store.Register("agent");
        store.Report("agent", AgentActivityState.Working, "api", "Task accepted");
        store.Report("agent", AgentActivityState.Blocked, "api", "Approval needed");
        store.Report("agent", AgentActivityState.Blocked, "api", "Approval needed");

        var notice = Assert.Single(store.Notifications("agent"));
        Assert.False(notice.Read);
        Assert.Equal(1, store.UnreadCount("agent"));
        Assert.Single(store.Notifications("agent"));
        Assert.True(store.AcknowledgeNotification(notice.Id));
        Assert.Equal(AgentActivityState.Blocked, store.Get("agent").State);
        Assert.Equal(0, store.UnreadCount("agent"));
    }

    /// <summary>
    /// 验证：确认当前的“完成”通知会把会话从 <see cref="AgentActivityState.Done"/> 转为 <see cref="AgentActivityState.Idle"/>，
    /// 来源记为 <c>ack</c>；再次确认同一条通知返回 <see langword="false"/>。
    /// </summary>
    [Fact]
    public void AcknowledgingCurrentCompletionMovesDoneToIdle()
    {
        var store = new AgentActivityStore(root);
        store.Register("agent");
        store.Report("agent", AgentActivityState.Done, "hook", "Turn finished");
        var notice = Assert.Single(store.Notifications());
        Assert.True(store.AcknowledgeNotification(notice.Id));
        Assert.Equal(AgentActivityState.Idle, store.Get("agent").State);
        Assert.Equal("ack", store.Get("agent").Source);
        Assert.False(store.AcknowledgeNotification(notice.Id));
    }

    /// <summary>
    /// 验证：看似完成的普通输出不会改变 <see cref="AgentActivityState.Unknown"/> 状态；被拆在两个输出块中的审批提示
    /// <c>Do you want to proceed?</c> 会被识别为等待决定（来源 <c>screen</c>）；而在 hook 显式上报工作中之后，
    /// 同样的提示文本不能再覆盖该显式状态。
    /// </summary>
    [Fact]
    public void ScreenHintsAreConservativeAndCannotOverrideExplicitReports()
    {
        var store = new AgentActivityStore(root);
        store.Register("agent");
        store.ObserveOutput("agent", "All tests passed. Task completed successfully.");
        Assert.Equal(AgentActivityState.Unknown, store.Get("agent").State);
        store.ObserveOutput("agent", "\r\nDo you want to pro");
        store.ObserveOutput("agent", "ceed?\r\n");
        Assert.Equal(AgentActivityState.Blocked, store.Get("agent").State);
        Assert.Equal("screen", store.Get("agent").Source);
        store.Report("agent", AgentActivityState.Working, "hook", "Tool approved externally");
        store.ObserveOutput("agent", "Do you want to proceed?\n");
        Assert.Equal(AgentActivityState.Working, store.Get("agent").State);
        Assert.Equal("hook", store.Get("agent").Source);
    }

    /// <summary>
    /// 验证：关注优先级按“等待决定 &gt; 未读完成 &gt; 工作中”排列；确认完成通知后，该会话的优先级降到工作中的会话之下。
    /// </summary>
    [Fact]
    public void PriorityPutsBlockedBeforeUnreadDoneAndAcknowledgedDoneBecomesOrdinary()
    {
        var store = new AgentActivityStore(root);
        store.Register("blocked"); store.Register("done"); store.Register("working");
        store.Report("blocked", AgentActivityState.Blocked, "api", "Needs decision");
        store.Report("done", AgentActivityState.Done, "api", "Finished");
        store.Report("working", AgentActivityState.Working, "api", "Running");
        Assert.True(store.Priority("blocked") > store.Priority("done"));
        Assert.True(store.Priority("done") > store.Priority("working"));
        store.AcknowledgeNotification(Assert.Single(store.Notifications("done")).Id);
        Assert.True(store.Priority("done") < store.Priority("working"));
    }

    /// <summary>
    /// 验证：定向消息只出现在收件会话的邮箱中并计入其未读数；重新打开存储后消息仍在；非收件人确认抛出 <see cref="ArgumentException"/>，
    /// 收件人确认成功且结果被持久化，重复确认返回 <see langword="false"/>；向未登记的会话发消息抛出 <see cref="KeyNotFoundException"/>。
    /// </summary>
    [Fact]
    public void DirectedMailboxAndAcknowledgementsSurviveReload()
    {
        var store = new AgentActivityStore(root);
        store.Register("sender"); store.Register("receiver"); store.Register("other");
        var message = store.SendMessage("sender", "receiver", "Please review the patch");
        Assert.Empty(store.Messages("other"));
        Assert.False(Assert.Single(store.Messages("receiver")).Acknowledged);
        Assert.Equal(1, store.UnreadCount("receiver"));
        var reopened = new AgentActivityStore(root);
        Assert.Equal(message.Id, Assert.Single(reopened.Messages("receiver")).Id);
        Assert.Throws<ArgumentException>(() => reopened.AcknowledgeMessage(message.Id, "other"));
        Assert.True(reopened.AcknowledgeMessage(message.Id, "receiver"));
        Assert.True(Assert.Single(new AgentActivityStore(root).Messages("receiver")).Acknowledged);
        Assert.False(reopened.AcknowledgeMessage(message.Id, "receiver"));
        Assert.Throws<KeyNotFoundException>(() => reopened.SendMessage("sender", "missing", "No implicit target"));
    }

    /// <summary>
    /// 验证：通知与消息上限都设为 2 时，连续产生 4 轮完成通知和消息后，重新打开得到的文件只保留最近 2 条通知
    /// 和最近 2 条消息（<c>Message 2</c>、<c>Message 3</c>），且被保留的通知仍为未读。
    /// </summary>
    [Fact]
    public void PersistedNotificationsAndMessagesAreBounded()
    {
        var store = new AgentActivityStore(root, maximumNotifications: 2, maximumMessages: 2);
        store.Register("a"); store.Register("b");
        for (var i = 0; i < 4; i++)
        {
            store.Report("a", AgentActivityState.Working, "api", "Started");
            store.Report("a", AgentActivityState.Done, "api", "Finished " + i);
            store.SendMessage("a", "b", "Message " + i);
        }
        var reopened = new AgentActivityStore(root);
        Assert.Equal(2, reopened.Notifications().Count);
        Assert.Equal(["Message 2", "Message 3"], reopened.Messages("b").Select(message => message.Text));
        Assert.All(reopened.Notifications(), notification => Assert.False(notification.Read));
    }

    /// <summary>
    /// 验证：<see cref="AgentActivityStore.WaitAsync"/> 在登记后的版本之上等待 <see cref="AgentActivityState.Done"/> 时，
    /// 随后的上报会使其返回完成状态；等待一直不会出现的状态时在 40 毫秒后抛出 <see cref="TimeoutException"/>；
    /// 等待期间取消令牌则以 <see cref="OperationCanceledException"/>（或其派生类型）结束。
    /// </summary>
    [Fact]
    public async Task WaitObservesNewRevisionAndSupportsTimeoutAndCancellation()
    {
        var store = new AgentActivityStore(root);
        var original = store.Register("agent");
        var wait = store.WaitAsync("agent", [AgentActivityState.Done], original.Revision, TimeSpan.FromSeconds(2));
        store.Report("agent", AgentActivityState.Done, "api", "Finished");
        Assert.Equal(AgentActivityState.Done, (await wait).State);
        await Assert.ThrowsAsync<TimeoutException>(() => store.WaitAsync("agent", [AgentActivityState.Blocked], null, TimeSpan.FromMilliseconds(40)));
        using var cancel = new CancellationTokenSource();
        var cancelled = store.WaitAsync("agent", [AgentActivityState.Working], null, TimeSpan.FromSeconds(2), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    /// <summary>
    /// 验证：终端进程以退出码 0 退出时，会话状态变为 <see cref="AgentActivityState.Exited"/>（来源 <c>process</c>），
    /// 不会被当作任务完成，也不生成任何通知。
    /// </summary>
    [Fact]
    public void ExitedProcessDoesNotPretendAnAgentTaskCompleted()
    {
        var store = new AgentActivityStore(root);
        store.Register("agent");
        store.MarkExited("agent", 0);
        Assert.Equal(AgentActivityState.Exited, store.Get("agent").State);
        Assert.Equal("process", store.Get("agent").Source);
        Assert.Empty(store.Notifications());
    }

    /// <summary>
    /// 验证：<c>agent-activity.json</c> 内容损坏时，构造存储抛出 <see cref="InvalidDataException"/>，文件原始内容保持不变。
    /// </summary>
    [Fact]
    public void CorruptStateIsReportedWithoutOverwritingOriginalBytes()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "agent-activity.json");
        File.WriteAllText(path, "{ damaged");
        Assert.Throws<InvalidDataException>(() => new AgentActivityStore(root));
        Assert.Equal("{ damaged", File.ReadAllText(path));
    }

    /// <summary>
    /// 验证：忘记一个会话会删除它的活动记录、通知以及它发出和收到的消息，而保留其他会话；
    /// 重新打开后只剩 <c>kept</c> 会话且没有残留通知与消息，再次忘记同一会话返回 <see langword="false"/>。
    /// </summary>
    [Fact]
    public void ForgetRemovesOnlyThatActorsActivityNotificationsAndRelatedMessages()
    {
        var store = new AgentActivityStore(root);
        store.Register("forgotten"); store.Register("kept");
        store.Report("forgotten", AgentActivityState.Done, "api", "Finished");
        store.SendMessage("forgotten", "kept", "Old outgoing");
        store.SendMessage("kept", "forgotten", "Old incoming");
        Assert.True(store.Forget("forgotten"));
        var reopened = new AgentActivityStore(root);
        Assert.Equal("kept", Assert.Single(reopened.List()).SessionId);
        Assert.Empty(reopened.Notifications());
        Assert.Empty(reopened.Messages("kept"));
        Assert.False(reopened.Forget("forgotten"));
    }

    /// <summary>
    /// 若状态目录已被创建，则递归删除它及其中所有文件。
    /// </summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
