namespace CrewTerm.Automation.Providers;

/// <summary>
/// 一个终端会话的持久化启动信息与原生对话身份，与是否有存活的进程无关。
/// </summary>
/// <remarks>
/// 保存在 <c>native-sessions.json</c>（<see cref="NativeSessionStore"/>）中，进程退出后仍保留，
/// 直到用户显式“忘记”该会话。恢复对话只使用这里记录的精确身份，绝不退回到“最近一次会话”之类的选择器。
/// </remarks>
/// <param name="Id">稳定的 CrewTerm 会话标识（按序数比较）。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Command">原始启动命令，保留用于显式“重新启动”（开启新对话）；不能包含 NUL。</param>
/// <param name="WorkingDirectory">本地项目目录；保存时规范化为不带末尾分隔符的绝对路径。</param>
/// <param name="Provider">Provider 标识（小写），例如 <c>claude</c>、<c>codex</c>；未知时为 <see langword="null"/>。</param>
/// <param name="NativeSessionId">Provider 侧精确的对话标识，绝不是 <c>--last</c>、<c>--continue</c> 之类的“最近会话”选择器；
/// 尚未捕获时为 <see langword="null"/>。</param>
/// <param name="ResumeCommand">用户显式登记的自定义恢复命令，或根据精确标识生成的恢复命令；不能包含 NUL，没有时为 <see langword="null"/>。</param>
/// <param name="UpdatedAt">元数据最后一次修改的时间（UTC），由存储在保存时写入；目录按它倒序列出。</param>
public sealed record NativeSessionRecord(string Id, string Name, string Command, string WorkingDirectory,
    string? Provider = null, string? NativeSessionId = null, string? ResumeCommand = null, DateTimeOffset? UpdatedAt = null);
