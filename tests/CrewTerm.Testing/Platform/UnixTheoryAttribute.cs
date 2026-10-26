namespace CrewTerm.Testing.Platform;

/// <summary>
/// 只在 Linux 与 macOS 上运行的数据驱动测试；Windows 上报告为跳过而不是失败。
/// </summary>
/// <remarks>
/// 与 <see cref="UnixFactAttribute"/> 相同，只是用于带 <c>InlineData</c> 等数据源的测试。
/// </remarks>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    /// <summary>
    /// 在 Windows 上设置跳过原因。
    /// </summary>
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix-specific behavior";
    }
}
