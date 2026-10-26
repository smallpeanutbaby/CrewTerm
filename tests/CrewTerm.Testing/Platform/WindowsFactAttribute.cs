namespace CrewTerm.Testing.Platform;

/// <summary>
/// 只在 Windows 上运行的测试；其他平台上报告为跳过而不是失败。
/// </summary>
/// <remarks>
/// 用于验证 Windows 专有行为的测试：ConPTY、作业对象、<c>cmd.exe</c> 引号规则、<c>.cmd</c> 启动脚本优先级等。
/// 跨平台行为应改用 <see cref="TestShell"/> 编写为普通测试，而不是标记为 Windows 专有。
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>
    /// 在非 Windows 平台上设置跳过原因。
    /// </summary>
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-specific behavior";
    }
}
