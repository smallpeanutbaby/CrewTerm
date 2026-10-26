using System.Runtime.InteropServices;
using System.Text;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// 伪控制台（ConPTY）、进程创建与作业对象所需的 kernel32 P/Invoke 声明和常量。
/// </summary>
/// <remarks>
/// 只在 <see cref="ConPtySession"/> 中使用；句柄的创建与释放顺序由调用方保证。
/// </remarks>
internal static class ConPtyNative
{
    /// <summary>
    /// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>：把伪控制台附加到新进程的属性号。
    /// </summary>
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>
    /// <c>STARTF_USESTDHANDLES</c>：使用 <see cref="StartupInfo"/> 中（全部为空的）标准句柄，
    /// 避免从无界面的 daemon 或测试宿主继承被重定向的句柄，由 ConPTY 提供控制台句柄。
    /// </summary>
    internal const int StartUseStdHandles = 0x00000100;

    /// <summary>
    /// <c>EXTENDED_STARTUPINFO_PRESENT</c>：启动信息为 <see cref="StartupInfoEx"/>。
    /// </summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>
    /// <c>CREATE_SUSPENDED</c>：先挂起主线程，等加入作业对象后再恢复，防止子进程逃出作业。
    /// </summary>
    internal const uint CreateSuspended = 0x00000004;

    /// <summary>
    /// <c>CREATE_UNICODE_ENVIRONMENT</c>：环境块为 UTF-16。
    /// </summary>
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>：关闭作业句柄时结束作业中的全部进程。
    /// </summary>
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    /// <summary>
    /// <c>JobObjectExtendedLimitInformation</c> 信息类别。
    /// </summary>
    internal const int JobObjectExtendedLimitInformationClass = 9;

    /// <summary>
    /// <c>WAIT_OBJECT_0</c>：等待的对象已处于有信号状态（进程已退出）。
    /// </summary>
    internal const uint WaitObject0 = 0;

    /// <summary>
    /// <c>ResumeThread</c> 失败时的返回值（<c>(DWORD)-1</c>）。
    /// </summary>
    internal const uint ResumeThreadFailed = uint.MaxValue;

    /// <summary>
    /// 创建匿名管道。
    /// </summary>
    /// <param name="readPipe">读端句柄。</param>
    /// <param name="writePipe">写端句柄。</param>
    /// <param name="securityAttributes">安全属性；零表示默认（不可继承）。</param>
    /// <param name="size">缓冲区大小建议值；零表示系统默认。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr securityAttributes, uint size);

    /// <summary>
    /// 创建伪控制台。
    /// </summary>
    /// <param name="size">初始列数与行数。</param>
    /// <param name="input">伪控制台读取输入的管道读端。</param>
    /// <param name="output">伪控制台写出输出的管道写端。</param>
    /// <param name="flags">创建标志；零表示默认。</param>
    /// <param name="pseudoConsole">伪控制台句柄。</param>
    /// <returns>HRESULT；0 表示成功。</returns>
    [DllImport("kernel32.dll")]
    internal static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    /// <summary>
    /// 调整伪控制台尺寸。
    /// </summary>
    /// <param name="pseudoConsole">伪控制台句柄。</param>
    /// <param name="size">新的列数与行数。</param>
    /// <returns>HRESULT；0 表示成功。</returns>
    [DllImport("kernel32.dll")]
    internal static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    /// <summary>
    /// 关闭伪控制台；调用期间伪控制台会把剩余输出写入输出管道，调用方必须继续读取以免阻塞。
    /// </summary>
    /// <param name="pseudoConsole">伪控制台句柄。</param>
    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    /// <summary>
    /// 初始化进程/线程属性列表；第一次以空列表调用用于获取所需大小。
    /// </summary>
    /// <param name="list">属性列表缓冲区。</param>
    /// <param name="count">属性个数。</param>
    /// <param name="flags">保留，必须为零。</param>
    /// <param name="size">输入缓冲区大小，输出所需大小。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);

    /// <summary>
    /// 设置属性列表中的一项属性。
    /// </summary>
    /// <param name="list">属性列表。</param>
    /// <param name="flags">保留，必须为零。</param>
    /// <param name="attribute">属性号。</param>
    /// <param name="value">属性值指针。</param>
    /// <param name="size">属性值大小。</param>
    /// <param name="previous">保留，必须为零。</param>
    /// <param name="returned">保留，必须为零。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

    /// <summary>
    /// 释放属性列表内部资源（缓冲区本身由调用方释放）。
    /// </summary>
    /// <param name="list">属性列表。</param>
    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>
    /// 创建进程（<c>CreateProcessW</c>）。
    /// </summary>
    /// <param name="applicationName">程序路径；为 <see langword="null"/> 时取命令行第一个参数。</param>
    /// <param name="commandLine">完整命令行（系统可能修改缓冲区，因此使用可写缓冲）。</param>
    /// <param name="processAttributes">进程安全属性。</param>
    /// <param name="threadAttributes">线程安全属性。</param>
    /// <param name="inheritHandles">是否继承句柄。</param>
    /// <param name="flags">创建标志。</param>
    /// <param name="environment">UTF-16 环境块。</param>
    /// <param name="currentDirectory">子进程工作目录。</param>
    /// <param name="startupInfo">扩展启动信息。</param>
    /// <param name="processInformation">返回的进程与主线程句柄。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    /// <summary>
    /// 关闭内核对象句柄。
    /// </summary>
    /// <param name="handle">句柄。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 读取进程退出码。
    /// </summary>
    /// <param name="process">进程句柄。</param>
    /// <param name="exitCode">退出码；进程仍在运行时为 <c>STILL_ACTIVE</c>。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>
    /// 等待对象变为有信号状态。
    /// </summary>
    /// <param name="handle">对象句柄。</param>
    /// <param name="milliseconds">超时毫秒数；零表示立即返回。</param>
    /// <returns><see cref="WaitObject0"/> 表示已有信号。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>
    /// 结束进程。
    /// </summary>
    /// <param name="process">进程句柄。</param>
    /// <param name="exitCode">退出码。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>
    /// 创建匿名作业对象。
    /// </summary>
    /// <param name="attributes">安全属性；零表示默认。</param>
    /// <param name="name">作业名；<see langword="null"/> 表示匿名。</param>
    /// <returns>作业句柄；失败时为零。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    /// <summary>
    /// 设置作业对象限制。
    /// </summary>
    /// <param name="job">作业句柄。</param>
    /// <param name="infoClass">信息类别。</param>
    /// <param name="info">扩展限制信息。</param>
    /// <param name="length">结构体字节数。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    /// <summary>
    /// 把进程加入作业对象。
    /// </summary>
    /// <param name="job">作业句柄。</param>
    /// <param name="process">进程句柄。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>
    /// 结束作业中的全部进程。
    /// </summary>
    /// <param name="job">作业句柄。</param>
    /// <param name="exitCode">各进程的退出码。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    /// <summary>
    /// 恢复挂起的线程。
    /// </summary>
    /// <param name="thread">线程句柄。</param>
    /// <returns>之前的挂起计数；失败时为 <see cref="ResumeThreadFailed"/>。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);
}
