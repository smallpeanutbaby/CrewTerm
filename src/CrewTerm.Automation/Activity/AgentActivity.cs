namespace CrewTerm.Automation.Activity;

/// <summary>
/// 某个会话的一次不可变活动观测：语义状态、证据来源、原因说明以及产生它的修改版本号。
/// </summary>
/// <remarks>
/// 由 <see cref="AgentActivityStore"/> 生成并持久化到 <c>agent-activity.json</c>。
/// 状态只来自显式事件（API 上报、Provider hook、用户确认）或已识别的审批提示，
/// 绝不根据终端进程是否存活来推测 Agent 是否在工作或已完成。
/// </remarks>
/// <param name="SessionId">CrewTerm 会话标识。</param>
/// <param name="State">语义活动状态，与终端进程是否存在相互独立。</param>
/// <param name="Source">证据来源标签，例如 <c>process</c>（登记/进程退出）、<c>hook</c>（Provider 回调）、
/// <c>screen</c>（识别到审批提示）、<c>ack</c>（用户确认完成）或调用方上报时指定的值；最长 64 个字符。</param>
/// <param name="Reason">面向用户的原因说明，最长 4096 个字符。</param>
/// <param name="ChangedAt">产生此观测的时间（UTC）。</param>
/// <param name="Revision">产生此观测时 <see cref="AgentActivitySnapshot.Revision"/> 的新值，
/// 供 <see cref="AgentActivityStore.WaitAsync"/> 判断是否出现了比调用方已知版本更新的变化。</param>
public sealed record AgentActivity(string SessionId, AgentActivityState State, string Source, string Reason,
    DateTimeOffset ChangedAt, long Revision);
