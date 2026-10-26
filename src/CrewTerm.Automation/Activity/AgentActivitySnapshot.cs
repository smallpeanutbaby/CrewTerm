namespace CrewTerm.Automation.Activity;

/// <summary>
/// <see cref="AgentActivityStore"/> 持久化到 <c>agent-activity.json</c> 的完整状态。
/// </summary>
/// <remarks>
/// 不可变：每次修改都生成新的快照并整体原子写入，读取方不会看到半截状态。
/// </remarks>
/// <param name="Revision">全局修改版本号，每次提交递增，供 <c>agent-wait</c> 判断“是否有更新的变化”。</param>
/// <param name="Activities">每个已登记会话的最新状态。</param>
/// <param name="Notifications">通知（只保留最近若干条）。</param>
/// <param name="Messages">会话间消息（只保留最近若干条）。</param>
internal sealed record AgentActivitySnapshot(long Revision, AgentActivity[] Activities, AgentNotification[] Notifications, AgentMessage[] Messages);
