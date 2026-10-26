using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>STARTUPINFOEXW</c>：启动信息加上进程/线程属性列表（用于附加伪控制台）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    /// <summary>
    /// 基本启动信息。
    /// </summary>
    internal StartupInfo StartupInfo;

    /// <summary>
    /// 已初始化的属性列表（含 <see cref="ConPtyNative.ProcThreadAttributePseudoConsole"/>）。
    /// </summary>
    internal IntPtr AttributeList;
}
