using CrewTerm.Automation.Activity;

namespace CrewTerm.App.Protocol;

/// <summary>
/// 一个终端会话（运行中或已保存的休眠会话）的对外描述。
/// </summary>
/// <param name="Id">会话标识。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Command">启动命令（重新启动时使用的原始命令）。</param>
/// <param name="WorkingDirectory">规范化的工作目录（项目根目录）。</param>
/// <param name="StartedAt">启动时间；休眠会话为最后一次更新记录的时间。</param>
/// <param name="HasExited">进程是否已退出；休眠会话为 <see langword="true"/>。</param>
/// <param name="ExitCode">进程退出码；仍在运行或未知时为 <see langword="null"/>。</param>
/// <param name="Activity">最近一次 Agent 语义状态。</param>
/// <param name="UnreadCount">未读通知与未确认消息的数量。</param>
/// <param name="Priority">排序优先级：等待决策 &gt; 未读完成 &gt; 未读消息 &gt; 工作中。</param>
/// <param name="Provider">Agent Provider，例如 <c>claude</c>；自定义命令为 <see langword="null"/>。</param>
/// <param name="NativeSessionId">已捕获的 Provider 原生会话标识。</param>
/// <param name="CanResume">是否可以精确恢复原生会话。</param>
/// <param name="ResumeReason">可以或不能恢复的原因说明。</param>
/// <param name="IsDormant">是否只有记录、没有正在运行的进程。</param>
public sealed record SessionInfo(string Id, string Name, string Command, string WorkingDirectory,
    DateTimeOffset StartedAt, bool HasExited, int? ExitCode, AgentActivity? Activity = null, int UnreadCount = 0,
    int Priority = 0, string? Provider = null, string? NativeSessionId = null, bool CanResume = false,
    string? ResumeReason = null, bool IsDormant = false);
