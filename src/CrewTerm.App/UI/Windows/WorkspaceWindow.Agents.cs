using CrewTerm.App.Protocol;
using CrewTerm.App.UI.Controls;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：Agent 协作——活动状态标签与提醒、通知中心、状态说明、Agent 之间的消息与交接、收件箱，以及等待指定状态。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 生成会话活动状态的简短标签，用于会话列表、面板标题与各种选择列表。
    /// </summary>
    /// <param name="session">会话信息。</param>
    /// <returns>
    /// 进程已退出时为 <c>EXIT</c> 加退出码；否则按活动状态为 <c>WORK</c>（工作中）、<c>WAIT</c>（等待用户）、
    /// <c>DONE*</c>（已完成且有未读通知）或 <c>DONE</c>、<c>IDLE</c>、<c>EXIT</c>；没有可靠的活动信息时为 <c>?</c>。
    /// </returns>
    private static string ActivityLabel(SessionInfo session) => session.HasExited ? "EXIT " + session.ExitCode
        : session.Activity?.State switch
        {
            AgentActivityState.Working => "WORK",
            AgentActivityState.Blocked => "WAIT",
            AgentActivityState.Done => session.UnreadCount > 0 ? "DONE*" : "DONE",
            AgentActivityState.Idle => "IDLE",
            AgentActivityState.Exited => "EXIT",
            _ => "?"
        };

    /// <summary>
    /// 检查各会话的活动状态是否有新的变化，并为需要关注的变化在状态栏提醒。
    /// </summary>
    /// <remarks>
    /// 按状态变化时间从早到晚处理，每个修订号只处理一次（记录在 <see cref="activityRevisions"/>）。
    /// 只有有未读通知且状态为 Done 或 Blocked 的变化才会提醒，因此最后显示的是最近一次需要关注的变化。
    /// </remarks>
    private void UpdateActivityAlerts()
    {
        foreach (var session in sessions.OrderBy(s => s.Activity?.ChangedAt))
        {
            if (session.Activity is not { } activity) continue;
            if (activityRevisions.GetValueOrDefault(session.Id) == activity.Revision) continue;
            activityRevisions[session.Id] = activity.Revision;
            if (session.UnreadCount > 0 && activity.State is AgentActivityState.Done or AgentActivityState.Blocked)
                status.Text = session.Name + ": " + activity.Reason + "  ·  F9 → Agent: Notifications";
        }
    }

    /// <summary>
    /// 打开通知列表：未读在前，同等条件下等待用户（Blocked）的在前，再按时间从新到旧排列；选中一条即聚焦对应会话并标记为已读。
    /// </summary>
    /// <remarks>
    /// 会话仍在列表中时先切换过去；用户因未保存修改取消切换时不标记已读。会话已不存在时只标记已读。
    /// </remarks>
    private void NotificationsMenu()
    {
        var notifications = Call(new("notifications")).Notifications ?? [];
        var ordered = notifications.OrderBy(n => n.Read).ThenBy(n => n.State == AgentActivityState.Blocked ? 0 : 1)
            .ThenByDescending(n => n.CreatedAt).ToArray();
        var labels = ordered.Select(n => $"{(n.Read ? "read" : "NEW")}  {SessionName(n.SessionId)}  ·  {n.State}  ·  {n.Reason}").ToArray();
        var choice = Dialogs.Choose(application, "NOTIFICATIONS · open to mark read", labels);
        if (choice < 0) return;
        var notice = ordered[choice];
        var session = sessions.FirstOrDefault(s => s.Id == notice.SessionId);
        if (session is not null && !FocusSession(session)) return;
        Call(new("notification-read", NotificationId: notice.Id));
        status.Text = "Selected " + SessionName(notice.SessionId) + "  ·  " + notice.Reason;
    }

    /// <summary>
    /// 按标识查找会话的显示名称。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <returns>会话在最近的会话列表中时为其名称，否则返回标识本身。</returns>
    private string SessionName(string id) => sessions.FirstOrDefault(s => s.Id == id)?.Name ?? id;

    /// <summary>
    /// 把会话显示到终端区并聚焦：必要时先切换到其所属项目；已显示在某个面板中时聚焦该面板，否则替换活动面板的会话。
    /// </summary>
    /// <param name="session">要显示的会话。</param>
    /// <returns>已显示并聚焦时为 <see langword="true"/>；用户因未保存修改取消切换项目时为 <see langword="false"/>。</returns>
    private bool FocusSession(SessionInfo session)
    {
        if (!BindSessionProject(session.WorkingDirectory)) return false;
        refreshGate.SelectionChanged();
        var pane = terminalLayout.Panes.FirstOrDefault(p => p.SessionId == session.Id) ?? ActiveTerminal;
        pane.SessionId = session.Id; pane.Update(null); pane.SetFocus();
        settings.LastSessionId = session.Id; terminalLayout.SaveBindings();
        return true;
    }

    /// <summary>
    /// 把活动面板会话的全部未读通知逐条标记为已读；活动面板未绑定会话时什么也不做。
    /// </summary>
    private void MarkSelectedRead()
    {
        if (ActiveTerminal.SessionId is not { } id) return;
        foreach (var notification in (Call(new("notifications", id)).Notifications ?? []).Where(n => !n.Read))
            Call(new("notification-read", NotificationId: notification.Id));
        status.Text = "Marked as read: " + SessionName(id);
    }

    /// <summary>
    /// 显示活动面板会话的活动状态详情（状态、来源、原因、变化时间与修订号）以及各状态标签的含义。
    /// </summary>
    /// <remarks>
    /// 活动面板未绑定会话，或 daemon 没有返回活动信息时什么也不做。
    /// </remarks>
    private void ExplainAgent()
    {
        if (ActiveTerminal.SessionId is not { } id) return;
        var activity = Call(new("agent-state", id)).Activity;
        if (activity is null) return;
        Dialogs.Text(application, "AGENT STATUS · " + SessionName(id),
            $"State: {activity.State}\nSource: {activity.Source}\nReason: {activity.Reason}\nChanged: {activity.ChangedAt:O}\nRevision: {activity.Revision}\n\n" +
            "WORK = explicit work in progress; WAIT = a user decision or input is required.\n" +
            "DONE* = completion not yet acknowledged; IDLE = acknowledged completion.\n" +
            "? = no reliable activity evidence. Process liveness alone does not imply work.");
    }

    /// <summary>
    /// 从活动面板的会话向另一个未退出的会话发送消息或交接提示。
    /// </summary>
    /// <param name="submit">
    /// 为 <see langword="true"/> 时发送交接提示：文本直接输入目标终端并按 Enter 提交；
    /// 为 <see langword="false"/> 时只把消息放入目标的收件箱，由对方自行读取。
    /// </param>
    /// <exception cref="InvalidOperationException">活动面板未绑定会话，或 daemon 拒绝发送。</exception>
    private void SendAgentMessage(bool submit)
    {
        var from = ActiveTerminal.SessionId ?? throw new InvalidOperationException("Select a sending Agent first");
        var targets = sessions.Where(s => s.Id != from && !s.HasExited).ToArray();
        var index = Dialogs.Choose(application, "Choose recipient", targets.Select(s => $"{s.Name}  ·  {ActivityLabel(s)}  ·  {s.Id}").ToArray());
        if (index < 0) return;
        var text = Dialogs.Prompt(application, submit ? "Handoff prompt" : "Send message",
            submit ? "Prompt to submit (sends Enter to the target)" : "Message text (recipient reads its inbox)");
        if (string.IsNullOrWhiteSpace(text)) return;
        var result = Call(new(submit ? "agent-handoff" : "message-send", FromSessionId: from, TargetSessionId: targets[index].Id, Text: text));
        status.Text = (submit ? "Prompt delivered" : "Message queued") + " to " + targets[index].Name + "  ·  " + result.Message?.Id;
    }

    /// <summary>
    /// 打开活动面板会话的收件箱：未读在前、按时间从新到旧；选中一条显示全文，并在关闭后确认（标记为已读）。
    /// </summary>
    /// <exception cref="InvalidOperationException">活动面板未绑定会话，或 daemon 调用失败。</exception>
    private void AgentInbox()
    {
        var id = ActiveTerminal.SessionId ?? throw new InvalidOperationException("Select an Agent first");
        var messages = Call(new("message-list", id)).Messages ?? [];
        var ordered = messages.OrderBy(m => m.Acknowledged).ThenByDescending(m => m.CreatedAt).ToArray();
        var index = Dialogs.Choose(application, "INBOX · " + SessionName(id), ordered.Select(m =>
            $"{(m.Acknowledged ? "read" : "NEW")}  {SessionName(m.FromSessionId)}  ·  {m.Text.Replace('\n', ' ')}").ToArray());
        if (index < 0) return;
        var message = ordered[index];
        Dialogs.Text(application, "INBOX MESSAGE", $"From: {SessionName(message.FromSessionId)}\nTo: {SessionName(id)}\n" +
            $"At: {message.CreatedAt:O}\nMessage: {message.Id}\nIn reply to: {message.InReplyTo ?? "—"}\n\n{message.Text}");
        Call(new("message-ack", id, MessageId: message.Id));
        status.Text = "Message marked read: " + message.Id;
    }

    /// <summary>
    /// 等待活动面板的会话进入所选的活动状态，并显示等待结果。
    /// </summary>
    /// <remarks>
    /// 依次询问目标状态与超时秒数（1–60，默认 30）。等待在后台进行，界面保持可用；
    /// 超时或失败时以消息框报告 daemon 返回的错误。
    /// </remarks>
    /// <exception cref="InvalidOperationException">daemon 版本较旧不支持该操作，或活动面板未绑定会话。</exception>
    /// <exception cref="ArgumentException">超时不是 1–60 之间的整数。</exception>
    private void WaitForAgent()
    {
        if (serverCompatibilityWarning is not null) throw new InvalidOperationException(serverCompatibilityWarning);
        var id = ActiveTerminal.SessionId ?? throw new InvalidOperationException("Select an Agent first");
        AgentActivityState[] states = [AgentActivityState.Done, AgentActivityState.Blocked,
            AgentActivityState.Idle, AgentActivityState.Working, AgentActivityState.Exited];
        var choice = Dialogs.Choose(application, "WAIT FOR STATE", states.Select(s => s.ToString()).ToArray());
        if (choice < 0) return;
        var timeout = Dialogs.Prompt(application, "Wait for Agent", "Wait timeout in seconds (1–60)", "30");
        if (timeout is null) return;
        if (!int.TryParse(timeout, out var seconds) || seconds is < 1 or > 60)
            throw new ArgumentException("Enter a number between 1 and 60.");
        var name = SessionName(id);
        RunWork("Waiting for " + name + "…", () => client.CallAsync(new("agent-wait", id,
            State: states[choice], TimeoutMilliseconds: seconds * 1000), lifetime.Token), response =>
        {
            if (!response.Ok || response.Activity is not { } activity)
                throw new InvalidOperationException(response.Error ?? "No Agent state was returned");
            Dialogs.Text(application, "WAIT RESULT · " + name,
                $"State: {activity.State}\nSource: {activity.Source}\nReason: {activity.Reason}\nChanged: {activity.ChangedAt:O}");
        });
    }
}
