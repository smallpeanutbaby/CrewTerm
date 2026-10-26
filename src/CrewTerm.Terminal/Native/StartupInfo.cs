using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>STARTUPINFOW</c>：<c>CreateProcessW</c> 的启动信息（Unicode 版本）。
/// </summary>
/// <remarks>
/// 只作为 <see cref="StartupInfoEx"/> 的第一个成员使用：<see cref="Size"/> 设为扩展结构的大小，
/// <see cref="Flags"/> 设为 <c>STARTF_USESTDHANDLES</c> 且三个标准句柄保持为零，使子进程只使用伪控制台。
/// 字段顺序与布局必须与 Win32 定义完全一致。
/// </remarks>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    /// <summary>
    /// 结构体字节数（<c>cb</c>）；使用扩展启动信息时为 <see cref="StartupInfoEx"/> 的大小。
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
    /// 保留指针与标准输入、输出、错误句柄（全部为零，由伪控制台提供控制台句柄）。
    /// </summary>
    internal IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
}
