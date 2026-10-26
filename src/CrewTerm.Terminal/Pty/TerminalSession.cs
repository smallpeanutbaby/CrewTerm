using System.Runtime.InteropServices;
using CrewTerm.Terminal.Native;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 按当前操作系统创建伪终端会话。
/// </summary>
public static class TerminalSession
{
    /// <summary>
    /// Windows 上 ConPTY 首次可用的系统版本（Windows 10 1809，内部版本 17763）。
    /// </summary>
    private const int ConPtyMinimumBuild = 17763;

    /// <summary>
    /// <see cref="DetachCurrentProcess"/> 无法脱离终端时注册的信号处理；保存引用使注册在进程生命周期内一直有效。
    /// </summary>
    private static readonly List<PosixSignalRegistration> TerminalSignals = [];

    /// <summary>
    /// 当前平台是否支持伪终端会话：Windows 10 1809 及以上、Linux 与 macOS。
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ConPtyMinimumBuild) || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// 在新的伪终端中启动命令。
    /// </summary>
    /// <remarks>
    /// 命令行开头的裸程序名只在 <c>PATH</c> 的绝对路径条目中查找，不查找工作目录；带目录的相对路径按工作目录解析。
    /// Windows 按 <c>CreateProcess</c> 的引号规则把其余部分原样传给程序；Linux 与 macOS 通过 <c>/bin/sh -c</c> 执行，
    /// 其余部分按 POSIX Shell 规则解释。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="commandLine">命令行。</param>
    /// <param name="workingDirectory">工作目录，必须已存在。</param>
    /// <param name="columns">初始列数（1–500）。</param>
    /// <param name="rows">初始行数（1–300）。</param>
    /// <param name="environment">在继承的环境变量之上追加或覆盖的变量；值为 <see langword="null"/> 表示删除该变量。</param>
    /// <returns>已启动的会话。</returns>
    /// <exception cref="PlatformNotSupportedException">当前平台不支持伪终端。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">找不到程序或系统调用失败。</exception>
    public static ITerminalSession Start(string id, string commandLine, string workingDirectory, int columns, int rows,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, ConPtyMinimumBuild))
            return ConPtySession.Start(id, commandLine, workingDirectory, columns, rows, environment);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return UnixPtySession.Start(id, commandLine, workingDirectory, columns, rows, environment);
        throw new PlatformNotSupportedException("Terminal sessions require Windows 10 1809 or later, Linux, or macOS.");
    }

    /// <summary>
    /// 让当前进程脱离启动它的终端，作为长期运行的后台进程使用（Linux / macOS；Windows 上不需要，直接返回）。
    /// </summary>
    /// <remarks>
    /// 创建新会话（关闭启动它的终端窗口时不会收到 <c>SIGHUP</c>），并把标准输入、输出、错误改接到 <c>/dev/null</c>，
    /// 不再持有启动者的管道。只应在进程启动之初调用一次。
    /// </remarks>
    public static void DetachCurrentProcess()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        UnixPtyNative.EnsureResolver();
        // 由 CrewTerm 自己启动的 daemon 不是进程组首进程，setsid 一定成功。直接从交互式 Shell 运行 --server 时它是前台进程组首进程，
        // setsid 失败、仍属于该终端：此时忽略终端发来的挂断、中断与退出信号，关闭窗口或按 Ctrl+C 不会结束 daemon。
        if (UnixPtyNative.CreateSession() < 0)
            foreach (var signal in new[] { PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
                TerminalSignals.Add(PosixSignalRegistration.Create(signal, context => context.Cancel = true));
        var devNull = UnixPtyNative.Open("/dev/null", UnixPtyNative.OpenReadWrite);
        if (devNull < 0) return;
        for (var target = 0; target <= 2; target++) UnixPtyNative.Duplicate(devNull, target);
        if (devNull > 2) UnixPtyNative.Close(devNull);
    }
}
