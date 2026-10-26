using System.Runtime.InteropServices;

namespace CrewTerm.App.Client.Native;

/// <summary>
/// Win32 <c>PROCESS_INFORMATION</c>：新进程与其主线程的句柄和标识。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    /// <summary>
    /// 进程句柄与主线程句柄；启动 daemon 后立即关闭，不跟踪其生命周期。
    /// </summary>
    internal IntPtr Process, Thread;

    /// <summary>
    /// 进程标识与主线程标识。
    /// </summary>
    internal int ProcessId, ThreadId;
}
