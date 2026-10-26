namespace CrewTerm.Automation.Activity;

/// <summary>
/// 本机会话之间的一条定向协作消息；列出消息不会把它标记为已确认。
/// </summary>
/// <remarks>
/// 消息只保存在 <c>agent-activity.json</c> 中，不会被自动输入到任何终端；
/// 只有收件会话通过 <see cref="AgentActivityStore.AcknowledgeMessage"/> 才能确认。
/// </remarks>
/// <param name="Id">消息标识（32 位十六进制 GUID，格式 <c>N</c>）。</param>
/// <param name="FromSessionId">发件会话标识，必须是已登记的会话。</param>
/// <param name="ToSessionId">收件会话标识，必须是已登记的会话。</param>
/// <param name="Text">消息正文，非空且最长 32768 个字符。</param>
/// <param name="CreatedAt">发送时间（UTC）。</param>
/// <param name="Acknowledged">收件方是否已显式确认。</param>
/// <param name="InReplyTo">所回复消息的标识；该消息必须是由本消息的收件方发给发件方的。不是回复时为 <see langword="null"/>。</param>
public sealed record AgentMessage(string Id, string FromSessionId, string ToSessionId, string Text,
    DateTimeOffset CreatedAt, bool Acknowledged, string? InReplyTo = null);
