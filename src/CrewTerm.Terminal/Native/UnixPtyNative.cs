using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Linux 与 macOS 伪终端所需的 libc P/Invoke 声明和常量。
/// </summary>
/// <remarks>
/// <para>库名经 <see cref="NativeLibrary.SetDllImportResolver"/> 映射（首次使用前调用 <see cref="EnsureResolver"/> 注册）：Linux 为 <c>libc.so.6</c>（<c>forkpty</c> 优先取
/// <c>libutil.so.1</c>，兼容 glibc 2.34 之前把它放在 libutil 中的系统），macOS 为 <c>libSystem</c>。</para>
/// <para><c>fork</c> 之后子进程只调用标记了 <see cref="SuppressGCTransitionAttribute"/> 的函数：这些调用不切换 GC 模式，
/// 不会在子进程中等待父进程里其他线程持有的运行时锁；它们的入口在 fork 之前由 <see cref="PrepareChildCalls"/> 预先绑定。</para>
/// <para><c>ioctl</c> 是可变参数函数。Apple Silicon 的调用约定把可变参数一律放在栈上，因此 macOS arm64 使用
/// <see cref="IoctlWindowSizeApple"/>：前 8 个整数参数占满寄存器，真正的参数第 9 个落到栈上，与 <c>va_arg</c> 的读取位置一致。</para>
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class UnixPtyNative
{
    /// <summary>
    /// 映射到系统 C 库的逻辑库名。
    /// </summary>
    private const string Libc = "crewterm-libc";

    /// <summary>
    /// 映射到提供 <c>forkpty</c> 的库的逻辑库名。
    /// </summary>
    private const string Libutil = "crewterm-libutil";

    /// <summary>
    /// <c>SIGHUP</c>：终端挂断，结束会话时首先发送。
    /// </summary>
    internal const int SignalHangup = 1;

    /// <summary>
    /// <c>SIGKILL</c>：进程组在宽限期后仍未退出时强制结束。
    /// </summary>
    internal const int SignalKill = 9;

    /// <summary>
    /// <c>SIGPIPE</c>：.NET 运行时在进程中忽略该信号，子进程执行前恢复为默认处理。
    /// </summary>
    internal const int SignalPipe = 13;

    /// <summary>
    /// <c>waitpid</c> 的 <c>WNOHANG</c>：子进程尚未退出时立即返回 0。
    /// </summary>
    internal const int WaitNoHang = 1;

    /// <summary>
    /// <c>EINTR</c>（Linux 与 macOS 均为 4）：系统调用被信号中断，应重试。
    /// </summary>
    internal const int ErrorInterrupted = 4;

    /// <summary>
    /// <c>ECHILD</c>（Linux 与 macOS 均为 10）：指定的子进程不存在或已被回收。
    /// </summary>
    internal const int ErrorNoChild = 10;

    /// <summary>
    /// 足以容纳任何平台 <c>sigset_t</c> 的字节数（Linux glibc 为 128 字节，macOS 为 4 字节）。
    /// </summary>
    internal const int SignalSetBytes = 128;

    /// <summary>
    /// fork 之后子进程恢复默认处理的信号编号上限（1–31，覆盖 Linux 与 macOS 的全部标准信号）。
    /// </summary>
    internal const int StandardSignalCount = 31;

    /// <summary>
    /// 是否已注册库名解析器（0 / 1）。
    /// </summary>
    /// <remarks>
    /// 不使用静态构造函数：本类因此没有类型初始化检查，fork 之后的子进程调用这里的函数时不会进入运行时。
    /// </remarks>
    private static int resolverRegistered;

    /// <summary>
    /// 注册库名解析器；可重复调用，只在第一次生效。必须在调用本类任何原生函数之前调用。
    /// </summary>
    internal static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref resolverRegistered, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(UnixPtyNative).Assembly, Resolve);
    }

    /// <summary>
    /// <c>sigprocmask</c> 的 <c>SIG_SETMASK</c>：Linux 为 2，macOS 为 3。
    /// </summary>
    internal static int SignalSetMask => OperatingSystem.IsMacOS() ? 3 : 2;

    /// <summary>
    /// <c>TIOCSWINSZ</c>：设置伪终端窗口尺寸的 ioctl 请求号。
    /// </summary>
    private static nuint SetWindowSizeRequest => OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

    /// <summary>
    /// 把逻辑库名解析为当前平台的系统库。
    /// </summary>
    /// <param name="name">DllImport 中的库名。</param>
    /// <param name="assembly">发起调用的程序集。</param>
    /// <param name="path">搜索路径设置。</param>
    /// <returns>库句柄；不是本类的逻辑库名时返回零，交给默认规则处理。</returns>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name is not (Libc or Libutil)) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["/usr/lib/libSystem.B.dylib", "libc"]
            : name == Libutil ? ["libutil.so.1", "libc.so.6", "libc"] : ["libc.so.6", "libc"];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }

    /// <summary>
    /// 创建伪终端并 fork：子进程成为新会话的首进程，伪终端从设备成为其控制终端与标准输入输出。
    /// </summary>
    /// <param name="master">父进程得到的主设备文件描述符。</param>
    /// <param name="name">从设备名缓冲区；传零表示不需要。</param>
    /// <param name="termios">终端属性；传零表示系统默认。</param>
    /// <param name="size">初始窗口尺寸。</param>
    /// <returns>父进程中为子进程标识，子进程中为 0，失败为 -1（读取 <see cref="Marshal.GetLastSystemError"/>）。</returns>
    [DllImport(Libutil, EntryPoint = "forkpty")]
    [SuppressGCTransition]
    internal static extern int ForkPty(out int master, IntPtr name, IntPtr termios, ref WindowSize size);

    /// <summary>
    /// 切换当前目录（只在 fork 后的子进程中调用）。
    /// </summary>
    /// <param name="path">以 NUL 结尾的 UTF-8 路径。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "chdir")]
    [SuppressGCTransition]
    internal static extern int ChangeDirectory(IntPtr path);

    /// <summary>
    /// 用新程序替换当前进程（只在 fork 后的子进程中调用）。
    /// </summary>
    /// <param name="path">以 NUL 结尾的 UTF-8 程序路径。</param>
    /// <param name="argv">以空指针结尾的参数指针数组。</param>
    /// <param name="envp">以空指针结尾的 <c>KEY=VALUE</c> 指针数组。</param>
    /// <returns>只在失败时返回 -1。</returns>
    [DllImport(Libc, EntryPoint = "execve")]
    [SuppressGCTransition]
    internal static extern int Execute(IntPtr path, IntPtr argv, IntPtr envp);

    /// <summary>
    /// 立即结束进程，不运行任何清理（只在 fork 后的子进程中调用）。
    /// </summary>
    /// <param name="status">退出码。</param>
    [DllImport(Libc, EntryPoint = "_exit")]
    [SuppressGCTransition]
    internal static extern void ExitImmediately(int status);

    /// <summary>
    /// 设置信号处理方式（只在 fork 后的子进程中调用，用于恢复默认处理）。
    /// </summary>
    /// <param name="signal">信号编号。</param>
    /// <param name="handler">处理函数；零表示 <c>SIG_DFL</c>。</param>
    /// <returns>原处理函数。</returns>
    [DllImport(Libc, EntryPoint = "signal")]
    [SuppressGCTransition]
    internal static extern IntPtr SetSignalHandler(int signal, IntPtr handler);

    /// <summary>
    /// 设置信号屏蔽字（只在 fork 后的子进程中调用，用于解除继承的屏蔽）。
    /// </summary>
    /// <param name="how">操作，见 <see cref="SignalSetMask"/>。</param>
    /// <param name="set">新的信号集。</param>
    /// <param name="old">保存旧信号集的缓冲区；传零表示不需要。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "sigprocmask")]
    [SuppressGCTransition]
    internal static extern int SetSignalMask(int how, IntPtr set, IntPtr old);

    /// <summary>
    /// 查询子进程状态。
    /// </summary>
    /// <param name="pid">子进程标识。</param>
    /// <param name="status">原始状态字。</param>
    /// <param name="options">选项，例如 <see cref="WaitNoHang"/>。</param>
    /// <returns>已退出时为子进程标识，尚未退出为 0，出错为 -1。</returns>
    [DllImport(Libc, EntryPoint = "waitpid", SetLastError = true)]
    internal static extern int WaitProcess(int pid, out int status, int options);

    /// <summary>
    /// 向进程或进程组发送信号。
    /// </summary>
    /// <param name="pid">进程标识；负数表示该编号的进程组。</param>
    /// <param name="signal">信号编号。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "kill", SetLastError = true)]
    internal static extern int SendSignal(int pid, int signal);

    /// <summary>
    /// <c>ioctl(fd, TIOCSWINSZ, &amp;size)</c>，用于 Linux 与 Intel macOS（可变参数与普通参数传递方式相同）。
    /// </summary>
    /// <param name="fd">主设备文件描述符。</param>
    /// <param name="request">请求号。</param>
    /// <param name="size">新的窗口尺寸。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlWindowSize(int fd, nuint request, ref WindowSize size);

    /// <summary>
    /// <c>ioctl(fd, TIOCSWINSZ, &amp;size)</c> 的 Apple Silicon 版本：六个占位参数填满剩余寄存器，使尺寸指针按可变参数约定位于栈上。
    /// </summary>
    /// <param name="fd">主设备文件描述符。</param>
    /// <param name="request">请求号。</param>
    /// <param name="r2">占位（寄存器 x2）。</param>
    /// <param name="r3">占位（寄存器 x3）。</param>
    /// <param name="r4">占位（寄存器 x4）。</param>
    /// <param name="r5">占位（寄存器 x5）。</param>
    /// <param name="r6">占位（寄存器 x6）。</param>
    /// <param name="r7">占位（寄存器 x7）。</param>
    /// <param name="size">新的窗口尺寸（第一个栈参数）。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlWindowSizeApple(int fd, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, ref WindowSize size);

    /// <summary>
    /// 不带第三个参数的 <c>ioctl(fd, request)</c>，例如 <c>FIOCLEX</c>；没有可变参数，各平台调用约定相同。
    /// </summary>
    /// <param name="fd">文件描述符。</param>
    /// <param name="request">请求号。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlNoArgument(int fd, nuint request);

    /// <summary>
    /// 为文件描述符设置 close-on-exec（<c>ioctl(fd, FIOCLEX)</c>），使它不被之后 <c>exec</c> 的子进程继承。
    /// </summary>
    /// <remarks>
    /// 用 <c>FIOCLEX</c> 而不是 <c>fcntl(F_SETFD)</c>：后者是可变参数函数，在 Apple Silicon 上需要特殊的参数布局。
    /// <c>FIOCLEX</c> 在 Linux 上为 <c>0x5451</c>，在 macOS 上为 <c>_IO('f', 1)</c>。
    /// </remarks>
    /// <param name="fd">文件描述符。</param>
    /// <returns>成功为 0，失败为 -1。</returns>
    internal static int SetCloseOnExec(int fd) => IoctlNoArgument(fd, OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u);

    /// <summary>
    /// 设置伪终端窗口尺寸；内核随后向前台进程组发送 <c>SIGWINCH</c>。
    /// </summary>
    /// <param name="fd">主设备文件描述符。</param>
    /// <param name="size">新的窗口尺寸。</param>
    /// <returns>成功为 0，失败为 -1（读取 <see cref="Marshal.GetLastPInvokeError"/>）。</returns>
    internal static int SetWindowSize(int fd, WindowSize size)
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlWindowSizeApple(fd, SetWindowSizeRequest, 0, 0, 0, 0, 0, 0, ref size)
            : IoctlWindowSize(fd, SetWindowSizeRequest, ref size);

    /// <summary>
    /// <c>open</c> 的 <c>O_RDWR</c>（Linux 与 macOS 相同）。
    /// </summary>
    internal const int OpenReadWrite = 2;

    /// <summary>
    /// 创建新会话并成为会话首进程，从而脱离原控制终端。
    /// </summary>
    /// <returns>新会话标识；已是进程组首进程时为 -1。</returns>
    [DllImport(Libc, EntryPoint = "setsid", SetLastError = true)]
    internal static extern int CreateSession();

    /// <summary>
    /// 打开文件（不创建，因此不传可变参数 <c>mode</c>，在 Apple Silicon 上同样安全）。
    /// </summary>
    /// <param name="path">路径。</param>
    /// <param name="flags">打开标志，例如 <see cref="OpenReadWrite"/>。</param>
    /// <returns>文件描述符，失败为 -1。</returns>
    [DllImport(Libc, EntryPoint = "open", SetLastError = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>
    /// 把文件描述符复制到指定编号（目标已打开时先关闭）。
    /// </summary>
    /// <param name="source">源文件描述符。</param>
    /// <param name="target">目标编号。</param>
    /// <returns>目标编号，失败为 -1。</returns>
    [DllImport(Libc, EntryPoint = "dup2", SetLastError = true)]
    internal static extern int Duplicate(int source, int target);

    /// <summary>
    /// 关闭文件描述符。
    /// </summary>
    /// <param name="fd">文件描述符。</param>
    /// <returns>成功为 0。</returns>
    [DllImport(Libc, EntryPoint = "close", SetLastError = true)]
    internal static extern int Close(int fd);

    /// <summary>
    /// 在 fork 之前绑定子进程要用到的全部函数入口，避免子进程中发生库加载或符号查找。
    /// </summary>
    internal static void PrepareChildCalls()
    {
        EnsureResolver();
        foreach (var name in new[] { nameof(ForkPty), nameof(ChangeDirectory), nameof(Execute), nameof(ExitImmediately), nameof(SetSignalHandler), nameof(SetSignalMask) })
            Marshal.Prelink(typeof(UnixPtyNative).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
    }
}
