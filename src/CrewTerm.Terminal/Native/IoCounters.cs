using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>IO_COUNTERS</c>：作业的 I/O 统计（只为满足结构体布局，不读取）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    /// <summary>
    /// 读、写与其他 I/O 的操作次数和字节数。
    /// </summary>
    internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
}
