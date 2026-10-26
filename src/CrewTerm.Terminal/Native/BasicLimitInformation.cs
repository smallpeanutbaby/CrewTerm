using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>：作业对象的基本限制。
/// </summary>
/// <remarks>
/// 作为 <see cref="ExtendedLimitInformation.Basic"/> 使用；CrewTerm 只设置 <see cref="LimitFlags"/> 中的 kill-on-close 标志，
/// 其余字段保持为零（不限制）。字段顺序与布局必须与 Win32 定义完全一致。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BasicLimitInformation
{
    /// <summary>
    /// 每个进程与整个作业的用户态 CPU 时间上限（100 纳秒单位）。
    /// </summary>
    internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;

    /// <summary>
    /// <c>JOB_OBJECT_LIMIT_*</c> 标志，例如 <see cref="ConPtyNative.JobObjectLimitKillOnJobClose"/>。
    /// </summary>
    internal uint LimitFlags;

    /// <summary>
    /// 工作集下限与上限。
    /// </summary>
    internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;

    /// <summary>
    /// 作业中同时存在的进程数上限。
    /// </summary>
    internal uint ActiveProcessLimit;

    /// <summary>
    /// 处理器亲和性掩码。
    /// </summary>
    internal UIntPtr Affinity;

    /// <summary>
    /// 优先级类别与调度类别。
    /// </summary>
    internal uint PriorityClass, SchedulingClass;
}
