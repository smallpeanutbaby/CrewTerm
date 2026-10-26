using CrewTerm.Core.Security;

namespace CrewTerm.Automation.Activity;

/// <summary>
/// <see cref="AgentActivityStore"/> 的通知与邮箱部分：列出与确认通知、计算未读数与优先级、收发会话间消息。
/// </summary>
/// <remarks>
/// 与主体部分共用同一把进程内锁和同一个快照；每次修改都递增版本号并整体原子写入。
/// 所有读取操作都不会改变已读或已确认状态，只有显式的确认调用才会。
/// </remarks>
public sealed partial class AgentActivityStore
{
    /// <summary>
    /// 列出通知（按生成顺序，最旧的在前），可只列某个会话的；不会把它们标记为已读。
    /// </summary>
    /// <param name="sessionId">只列出该会话的通知；为 <see langword="null"/> 时列出全部。</param>
    /// <returns>通知数组的副本。</returns>
    public IReadOnlyList<AgentNotification> Notifications(string? sessionId = null)
    {
        lock (gate) return data.Notifications.Where(item => sessionId is null || item.SessionId == sessionId).ToArray();
    }

    /// <summary>
    /// 把一条通知标记为已读；确认当前的完成通知会把会话从 <see cref="AgentActivityState.Done"/> 变为 <see cref="AgentActivityState.Idle"/>。
    /// </summary>
    /// <remarks>
    /// 只有同时满足以下条件才切换为空闲（来源 <c>ack</c>）：被确认的是“完成”通知、会话当前仍处于
    /// <see cref="AgentActivityState.Done"/>、并且该会话已没有其他未读的“完成”通知。
    /// </remarks>
    /// <param name="notificationId">通知标识。</param>
    /// <returns>找到且原先未读时返回 <see langword="true"/>；不存在或已读时返回 <see langword="false"/> 且不修改文件。</returns>
    public bool AcknowledgeNotification(string notificationId)
    {
        lock (gate)
        {
            var notification = data.Notifications.FirstOrDefault(item => item.Id == notificationId);
            if (notification is null || notification.Read) return false;
            // 先确认会话存在再提交，避免状态文件不一致时“已读”已经落盘、随后的状态迁移却抛出异常。
            var sessionState = notification.State == AgentActivityState.Done ? Find(notification.SessionId).State : (AgentActivityState?)null;
            Commit(data with { Revision = data.Revision + 1, Notifications = data.Notifications.Select(item => item.Id == notificationId ? item with { Read = true } : item).ToArray() });
            if (notification.State == AgentActivityState.Done && sessionState == AgentActivityState.Done
                && !data.Notifications.Any(item => item.SessionId == notification.SessionId && item.State == AgentActivityState.Done && !item.Read))
                Transition(notification.SessionId, AgentActivityState.Idle, "ack", "Completion explicitly acknowledged");
            return true;
        }
    }

    /// <summary>
    /// 统计某个会话的未读通知数与未确认的收件消息数之和。
    /// </summary>
    /// <param name="sessionId">CrewTerm 会话标识；未登记的会话返回 0 而不是抛出异常。</param>
    /// <returns>未读通知数加未确认收件消息数。</returns>
    public int UnreadCount(string sessionId)
    {
        lock (gate) return data.Notifications.Count(item => item.SessionId == sessionId && !item.Read)
            + data.Messages.Count(item => item.ToSessionId == sessionId && !item.Acknowledged);
    }

    /// <summary>
    /// 计算会话的关注优先级：等待决定的排在未读完成、未确认消息和普通工作中的 Agent 之前。
    /// </summary>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <returns>
    /// 按先命中者计：<see cref="AgentActivityState.Blocked"/> 为 300；有未读完成通知为 200；
    /// 有未确认收件消息为 100；<see cref="AgentActivityState.Working"/> 为 50；其余为 0。数值越大越需要关注。
    /// </returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public int Priority(string sessionId)
    {
        lock (gate)
        {
            var current = Find(sessionId);
            if (current.State == AgentActivityState.Blocked) return 300;
            if (data.Notifications.Any(item => item.SessionId == sessionId && item.State == AgentActivityState.Done && !item.Read)) return 200;
            if (data.Messages.Any(item => item.ToSessionId == sessionId && !item.Acknowledged)) return 100;
            return current.State == AgentActivityState.Working ? 50 : 0;
        }
    }

    /// <summary>
    /// 向一个明确的已知收件会话持久化一条消息；不会把消息输入到任何终端。
    /// </summary>
    /// <param name="fromSessionId">已登记的发件会话标识。</param>
    /// <param name="toSessionId">已登记的收件会话标识。</param>
    /// <param name="text">消息正文，非空白且最长 32768 个字符。</param>
    /// <param name="inReplyTo">所回复消息的标识；该消息必须是由 <paramref name="toSessionId"/> 发给
    /// <paramref name="fromSessionId"/> 的。不是回复时为 <see langword="null"/>。</param>
    /// <returns>新建的未确认消息。</returns>
    /// <exception cref="ArgumentException">正文为空白、超长、含有换行与制表符以外的控制字符，或 <paramref name="inReplyTo"/> 不指向对方发给自己的消息。</exception>
    /// <exception cref="KeyNotFoundException">发件或收件会话尚未登记。</exception>
    public AgentMessage SendMessage(string fromSessionId, string toSessionId, string text, string? inReplyTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 32768) throw new ArgumentException("Message exceeds 32768 characters", nameof(text));
        // 消息正文会显示在收件人的工作台中；允许换行与制表符，拒绝 ESC 等可能被宿主终端执行的控制字符。
        if (DisplayText.ContainsControl(text, allowLineBreaks: true))
            throw new ArgumentException("Message must not contain control characters other than line breaks and tabs", nameof(text));
        lock (gate)
        {
            _ = Find(fromSessionId); _ = Find(toSessionId);
            if (inReplyTo is not null && !data.Messages.Any(item => item.Id == inReplyTo && item.ToSessionId == fromSessionId && item.FromSessionId == toSessionId))
                throw new ArgumentException("Reply must reference a message from the target to this sender", nameof(inReplyTo));
            var message = new AgentMessage(Guid.NewGuid().ToString("N"), fromSessionId, toSessionId, text, DateTimeOffset.UtcNow, false, inReplyTo);
            Commit(data with { Revision = data.Revision + 1, Messages = [.. data.Messages, message] });
            return message;
        }
    }

    /// <summary>
    /// 返回指定收件会话的邮箱（含已确认的消息，按发送顺序）；不会改变确认状态。
    /// </summary>
    /// <param name="toSessionId">已登记的收件会话标识。</param>
    /// <returns>发给该会话的消息数组副本（受保留条数上限限制）。</returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public IReadOnlyList<AgentMessage> Messages(string toSessionId)
    {
        lock (gate)
        {
            _ = Find(toSessionId);
            return data.Messages.Where(item => item.ToSessionId == toSessionId).ToArray();
        }
    }

    /// <summary>
    /// 代表消息的明确收件方确认一条消息；其他会话不能代为确认。
    /// </summary>
    /// <param name="messageId">消息标识。</param>
    /// <param name="toSessionId">发起确认的会话标识，必须与消息的收件方一致。</param>
    /// <returns>找到且原先未确认时返回 <see langword="true"/>；不存在或已确认时返回 <see langword="false"/> 且不修改文件。</returns>
    /// <exception cref="ArgumentException">消息属于其他收件方。</exception>
    public bool AcknowledgeMessage(string messageId, string toSessionId)
    {
        lock (gate)
        {
            var message = data.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null) return false;
            if (message.ToSessionId != toSessionId) throw new ArgumentException("Message belongs to another recipient", nameof(toSessionId));
            if (message.Acknowledged) return false;
            Commit(data with { Revision = data.Revision + 1, Messages = data.Messages.Select(item => item.Id == messageId ? item with { Acknowledged = true } : item).ToArray() });
            return true;
        }
    }
}
