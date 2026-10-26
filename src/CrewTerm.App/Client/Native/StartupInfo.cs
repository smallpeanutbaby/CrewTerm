using System.Runtime.InteropServices;

namespace CrewTerm.App.Client.Native;

/// <summary>
/// Win32 <c>STARTUPINFOW</c>，用于以不继承任何句柄的方式启动 daemon。
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    /// <summary>
    /// 结构体字节数（<c>cb</c>）。
    /// </summary>
    internal int Size;

    /// <summary>
    /// 保留字段、桌面名与窗口标题（均不使用）。
    /// </summary>
    internal IntPtr Reserved, Desktop, Title;

    /// <summary>
    /// 窗口位置、尺寸、填充属性与 <c>STARTF_*</c> 标志。
    /// </summary>
    internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;

    /// <summary>
    /// 窗口显示方式与保留字段。
    /// </summary>
    internal short ShowWindow, Reserved2;

    /// <summary>
    /// 保留指针与标准输入、输出、错误句柄（全部为零，即不传递任何句柄）。
    /// </summary>
    internal IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
}
