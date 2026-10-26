namespace CrewTerm.Automation.Activity;

/// <summary>
/// 持久化的会话通知；已读状态只能通过显式确认改变，列出或查看都不会改变它。
/// </summary>
/// <remarks>
/// 当会话状态从其他状态变为 <see cref="AgentActivityState.Blocked"/> 或 <see cref="AgentActivityState.Done"/> 时，
/// 由 <see cref="AgentActivityStore"/> 自动生成；只保留最近若干条（默认 200 条）。
/// </remarks>
/// <param name="Id">通知标识（32 位十六进制 GUID，格式 <c>N</c>）。</param>
/// <param name="SessionId">触发通知的会话标识。</param>
/// <param name="State">触发通知的新状态（<see cref="AgentActivityState.Blocked"/> 或 <see cref="AgentActivityState.Done"/>）。</param>
/// <param name="Title">简短标题：<c>Needs input</c> 或 <c>Completed</c>。</param>
/// <param name="Reason">与对应活动观测相同的原因说明。</param>
/// <param name="CreatedAt">生成时间（UTC），与对应观测的 <see cref="AgentActivity.ChangedAt"/> 相同。</param>
/// <param name="Read">是否已被显式确认为已读。</param>
/// <param name="Source">产生对应状态变化的证据来源标签（见 <see cref="AgentActivity.Source"/>）。</param>
public sealed record AgentNotification(string Id, string SessionId, AgentActivityState State, string Title,
    string Reason, DateTimeOffset CreatedAt, bool Read, string Source);
