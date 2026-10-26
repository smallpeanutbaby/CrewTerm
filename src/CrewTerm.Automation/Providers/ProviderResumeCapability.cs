namespace CrewTerm.Automation.Providers;

/// <summary>
/// 说明一条持久化会话记录是否包含足够的信息来恢复原生对话。
/// </summary>
/// <param name="CanResume">能否准备出精确的原生恢复命令（显式登记的恢复命令，或已校验的原生标识加可解析的启动参数）。</param>
/// <param name="Reason">面向用户的说明；不能恢复时指出缺少什么以及如何补全。</param>
public sealed record ProviderResumeCapability(bool CanResume, string Reason);
