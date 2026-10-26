using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>：作业对象的扩展限制。
/// </summary>
/// <remarks>
/// 以信息类别 <see cref="ConPtyNative.JobObjectExtendedLimitInformationClass"/>（9）传给 <c>SetInformationJobObject</c>，
/// 使作业句柄关闭时结束作业中的全部进程。字段顺序与布局必须与 Win32 定义完全一致。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ExtendedLimitInformation
{
    /// <summary>
    /// 基本限制（其中的 kill-on-close 标志是唯一使用的设置）。
    /// </summary>
    internal BasicLimitInformation Basic;

    /// <summary>
    /// I/O 统计（只读，不使用）。
    /// </summary>
    internal IoCounters Io;

    /// <summary>
    /// 进程与作业的内存上限，以及峰值内存用量。
    /// </summary>
    internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
}
