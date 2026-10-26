namespace CrewTerm.Testing.Platform;

/// <summary>
/// 只在 Windows 上运行的数据驱动测试；其他平台上报告为跳过而不是失败。
/// </summary>
/// <remarks>
/// 与 <see cref="WindowsFactAttribute"/> 相同，只是用于带 <c>InlineData</c> 等数据源的测试。
/// </remarks>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    /// <summary>
    /// 在非 Windows 平台上设置跳过原因。
    /// </summary>
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-specific behavior";
    }
}
