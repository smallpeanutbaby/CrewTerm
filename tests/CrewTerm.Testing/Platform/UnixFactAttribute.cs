namespace CrewTerm.Testing.Platform;

/// <summary>
/// 只在 Linux 与 macOS 上运行的测试；Windows 上报告为跳过而不是失败。
/// </summary>
/// <remarks>
/// 用于验证 <c>forkpty</c> 伪终端、POSIX 信号、执行权限与 <c>/bin/sh</c> 引号规则等 Unix 专有行为。
/// </remarks>
public sealed class UnixFactAttribute : FactAttribute
{
    /// <summary>
    /// 在 Windows 上设置跳过原因。
    /// </summary>
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix-specific behavior";
    }
}
