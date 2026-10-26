using CrewTerm.Automation.Providers;

namespace CrewTerm.Automation.Hooks;

/// <summary>
/// 仅用于本次启动的 hook 接入结果：实际交给终端执行的命令，以及是否接入了自动回调。
/// </summary>
/// <remarks>
/// 改写后的命令只用于这一次启动；持久化的原始启动命令（<see cref="NativeSessionRecord.Command"/>）保持不变，
/// 仍作为日后重新启动的模板，因此不会把一次性的 hook 参数写进会话目录。
/// </remarks>
/// <param name="Command">实际执行的命令：接入成功时附加了 <c>--settings</c>（Claude）或 <c>-c notify=...</c>（Codex），
/// 未接入时与原始命令完全相同。</param>
/// <param name="Enabled">是否已为本次启动接入自动生命周期回调。</param>
/// <param name="Reason">面向用户的说明：接入了什么，或为什么没有接入（例如保留了用户已有的配置）。</param>
/// <param name="SettingsPath">为 Claude 生成的单次 hook 设置文件的绝对路径；Codex 或未接入时为 <see langword="null"/>。</param>
public sealed record AgentHookLaunch(string Command, bool Enabled, string Reason, string? SettingsPath = null);
