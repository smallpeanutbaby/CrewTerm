namespace CrewTerm.Automation.Providers;

/// <summary>
/// <see cref="ProviderSessionService"/> 准备好的一次具体启动：要执行的命令，以及（已知时）它选中的精确原生对话身份。
/// </summary>
/// <param name="Command">要交给终端执行的命令；对 Claude 新对话可能已追加 <c>--session-id</c>，
/// 对恢复则是生成或登记的恢复命令。</param>
/// <param name="Provider">规范化（小写）后的 Provider 标识；无法识别时为 <see langword="null"/>。</param>
/// <param name="NativeSessionId">精确的原生对话标识；无法确定时为 <see langword="null"/>。</param>
/// <param name="ResumeCommand">可用于日后恢复此对话的具体命令；不可用时为 <see langword="null"/>。</param>
/// <param name="CanResume">之后能否准备出精确的原生恢复命令。</param>
/// <param name="ResumeReason">面向用户的说明：为什么可以或不可以恢复。</param>
public sealed record ProviderLaunchPlan(string Command, string? Provider, string? NativeSessionId,
    string? ResumeCommand, bool CanResume, string ResumeReason);
