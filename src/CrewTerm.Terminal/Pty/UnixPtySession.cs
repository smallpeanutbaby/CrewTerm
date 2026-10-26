using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CrewTerm.Terminal.Emulation;
using CrewTerm.Terminal.Native;
using Microsoft.Win32.SafeHandles;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 持有一个 Linux / macOS 伪终端（<c>forkpty</c>）及其进程组，与任何 UI 无关。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>子进程是新会话的首进程，伪终端从设备是它的控制终端，因此 Ctrl+C、作业控制与 <c>SIGWINCH</c> 都按真实终端工作。</description></item>
/// <item><description>命令经 <c>/bin/sh -c "exec …"</c> 执行：Shell 只负责解析引号与参数，随后被目标程序替换，进程标识即程序本身。</description></item>
/// <item><description>释放时先向整个进程组发送 <c>SIGHUP</c>，宽限期后发送 <c>SIGKILL</c>，并回收子进程，不留下僵尸进程。
/// 自行创建新会话或进程组的后代进程不在此范围内。</description></item>
/// <item><description>写入、resize、退出状态查询与释放由同一把锁串行化，可以从任意线程调用。</description></item>
/// </list>
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class UnixPtySession : ITerminalSession
{
    /// <summary>
    /// 主设备每次读取的字节数。
    /// </summary>
    private const int ReadBufferBytes = 16384;

    /// <summary>
    /// 发送 <c>SIGHUP</c> 后等待进程组退出的时间（毫秒），超时后发送 <c>SIGKILL</c>。
    /// </summary>
    private const int HangupGraceMilliseconds = 1000;

    /// <summary>
    /// 释放时等待读取线程收尾的最长时间（毫秒）。
    /// </summary>
    private const int DisposeWaitMilliseconds = 2000;

    /// <summary>
    /// 串行化所有会话的 <c>forkpty</c> 与随后的 close-on-exec 设置。
    /// </summary>
    private static readonly object ForkGate = new();

    /// <summary>
    /// 保护进程状态、写入与释放的锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 主设备文件描述符。
    /// </summary>
    private readonly int _master;

    /// <summary>
    /// 写入主设备的流（不拥有文件描述符）。
    /// </summary>
    private readonly FileStream _input;

    /// <summary>
    /// 读取主设备的流（拥有文件描述符，最后关闭）。
    /// </summary>
    private readonly FileStream _output;

    /// <summary>
    /// 持续读取输出的后台线程。
    /// </summary>
    private readonly Thread _reader;

    /// <summary>
    /// 是否已释放。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 子进程是否已被回收（之后不能再对该进程标识调用 <c>waitpid</c> 或发送信号）。
    /// </summary>
    private bool _reaped;

    /// <summary>
    /// 已观察到的退出码；仍在运行或无法获知时为 <see langword="null"/>。
    /// </summary>
    private int? _exitCode;

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string CommandLine { get; }

    /// <inheritdoc/>
    public string WorkingDirectory { get; }

    /// <inheritdoc/>
    public int ProcessId { get; }

    /// <inheritdoc/>
    public TerminalScreen Screen { get; }

    /// <inheritdoc/>
    public event Action<string>? OutputReceived;

    /// <inheritdoc/>
    public bool HasExited
    {
        get { lock (_gate) { RefreshExitCode(); return _disposed || _reaped; } }
    }

    /// <inheritdoc/>
    public int? ExitCode
    {
        get { lock (_gate) { RefreshExitCode(); return _exitCode; } }
    }

    /// <summary>
    /// 接管已创建的主设备与子进程并启动输出读取线程。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="commandLine">原始命令行。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="columns">初始列数。</param>
    /// <param name="rows">初始行数。</param>
    /// <param name="master">主设备文件描述符（所有权转移给本对象）。</param>
    /// <param name="pid">子进程标识。</param>
    private UnixPtySession(string id, string commandLine, string cwd, int columns, int rows, int master, int pid)
    {
        Id = id;
        CommandLine = commandLine;
        WorkingDirectory = cwd;
        ProcessId = pid;
        Screen = new(columns, rows);
        _master = master;
        _output = new(new SafeFileHandle(master, ownsHandle: true), FileAccess.Read, 0);
        _input = new(new SafeFileHandle(master, ownsHandle: false), FileAccess.Write, 0);
        Screen.ResponseRequested += Reply;
        _reader = new(ReadOutput) { IsBackground = true, Name = $"CrewTerm PTY {id}" };
        _reader.Start();
    }

    /// <summary>
    /// 在新的伪终端中启动命令。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="commandLine">命令行；程序路径的解析规则见 <see cref="TerminalCommand.ResolveUnix"/>。</param>
    /// <param name="workingDirectory">工作目录，必须已存在。</param>
    /// <param name="columns">初始列数（1–500）。</param>
    /// <param name="rows">初始行数（1–300）。</param>
    /// <param name="environment">在继承的环境变量之上追加或覆盖的变量；值为 <see langword="null"/> 表示删除该变量。</param>
    /// <returns>已启动的会话。</returns>
    /// <exception cref="PlatformNotSupportedException">不是 Linux 或 macOS。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    /// <exception cref="Win32Exception">找不到程序或 <c>forkpty</c> 失败。</exception>
    public static UnixPtySession Start(string id, string commandLine, string workingDirectory, int columns, int rows,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Unix pseudoterminals require Linux or macOS.");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        TerminalScreen.ValidateSize(columns, rows);
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        var argv = TerminalCommand.ResolveUnix(commandLine, cwd);
        var size = new WindowSize((ushort)rows, (ushort)columns, 0, 0);
        using var block = new ChildProcessBlock(argv, CreateEnvironment(environment), cwd);
        // fork 之前取好子进程要用的全部值并绑定原生入口：子进程只执行下面 if 块中预先绑定、不切换 GC 模式的原生调用，
        // 不调用任何托管方法（避免在只剩一个线程的子进程中触发 JIT 或等待运行时锁），也不分配托管内存。
        var program = block.Program;
        var arguments = block.Arguments;
        var childEnvironment = block.Environment;
        var directory = block.Directory;
        var emptySignalSet = block.EmptySignalSet;
        var setMask = UnixPtyNative.SignalSetMask;
        UnixPtyNative.PrepareChildCalls();
        int pid, master;
        // forkpty 创建的主设备没有 close-on-exec：在设置之前若另一个会话 fork，它的子进程会继承这个主设备，
        // 可以读写本会话的终端，并使从设备在本会话结束后仍不关闭。串行化启动，保证设置完成后才可能有下一次 fork。
        lock (ForkGate)
        {
            pid = UnixPtyNative.ForkPty(out master, IntPtr.Zero, IntPtr.Zero, ref size);
            if (pid == 0)
            {
                UnixPtyNative.SetSignalHandler(UnixPtyNative.SignalPipe, IntPtr.Zero);
                UnixPtyNative.SetSignalMask(setMask, emptySignalSet, IntPtr.Zero);
                UnixPtyNative.ChangeDirectory(directory);
                UnixPtyNative.Execute(program, arguments, childEnvironment);
                UnixPtyNative.ExitImmediately(127);
            }
            if (pid < 0) throw new Win32Exception(Marshal.GetLastSystemError(), "forkpty failed");
            UnixPtyNative.SetCloseOnExec(master);
        }
        try { return new UnixPtySession(id, commandLine, cwd, columns, rows, master, pid); }
        catch
        {
            UnixPtyNative.SendSignal(-pid, UnixPtyNative.SignalKill);
            UnixPtyNative.WaitProcess(pid, out _, 0);
            throw;
        }
    }

    /// <summary>
    /// 生成子进程的环境变量：继承当前进程的环境，声明本伪终端的终端能力与 UTF-8 区域设置，再应用调用方的覆盖。
    /// </summary>
    /// <param name="overrides">追加、覆盖（非 <see langword="null"/> 值）或删除（<see langword="null"/> 值）的变量。</param>
    /// <returns><c>KEY=VALUE</c> 形式的环境变量列表。</returns>
    /// <exception cref="ArgumentException">变量名为空或包含 <c>=</c>、NUL，或变量值包含 NUL。</exception>
    private static List<string> CreateEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string)entry.Value!;
        values["TERM"] = "xterm-256color";
        values["COLORTERM"] = "truecolor";
        // 从图形界面或服务管理器启动时可能没有区域设置，程序会退回 ASCII 输出；此时补一个 UTF-8 区域。
        if (!values.ContainsKey("LANG") && !values.ContainsKey("LC_ALL") && !values.ContainsKey("LC_CTYPE"))
            values["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (string.IsNullOrEmpty(key) || key.IndexOfAny(['=', '\0']) >= 0 || value?.Contains('\0') == true)
                throw new ArgumentException("Environment keys must be nonempty and cannot contain '=' or NUL; values cannot contain NUL.", nameof(overrides));
            if (value is null) values.Remove(key);
            else values[key] = value;
        }
        return values.Select(pair => pair.Key + "=" + pair.Value).ToList();
    }

    /// <inheritdoc/>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshExitCode();
            if (_reaped) throw new InvalidOperationException("The terminal process has exited.");
            _input.Write(Screen.EncodeInput(text));
        }
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows)
    {
        TerminalScreen.ValidateSize(columns, rows);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (UnixPtyNative.SetWindowSize(_master, new WindowSize((ushort)rows, (ushort)columns, 0, 0)) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot resize the pseudoterminal");
            Screen.Resize(columns, rows);
        }
    }

    /// <summary>
    /// 若子进程已退出则回收它并记录退出码。调用方必须持有 <see cref="_gate"/>。
    /// </summary>
    private void RefreshExitCode() => Reap(UnixPtyNative.WaitNoHang);

    /// <summary>
    /// 调用 <c>waitpid</c> 回收子进程并解码退出状态：正常退出取退出码，被信号结束为 128 加信号编号。
    /// 调用方必须持有 <see cref="_gate"/>。
    /// </summary>
    /// <param name="options"><c>waitpid</c> 选项；<see cref="UnixPtyNative.WaitNoHang"/> 表示不阻塞。</param>
    private void Reap(int options)
    {
        if (_reaped) return;
        var result = UnixPtyNative.WaitProcess(ProcessId, out var status, options);
        if (result == ProcessId)
        {
            _reaped = true;
            var signal = status & 0x7f;
            _exitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
        }
        // 子进程已被他人回收（ECHILD）：视为已退出，退出码未知。
        else if (result < 0) _reaped = true;
    }

    /// <summary>
    /// 把终端查询的回复写回进程；进程已退出或会话已释放时忽略。
    /// </summary>
    /// <param name="text">回复序列。</param>
    private void Reply(string text)
    {
        try { Write(text); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>
    /// 读取线程主体：解码 UTF-8 输出（跨块保留不完整的字节序列），更新屏幕并通知订阅者，直到从设备全部关闭。
    /// </summary>
    private void ReadOutput()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[ReadBufferBytes];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int count;
            while ((count = _output.Read(bytes)) != 0)
            {
                var text = new string(chars, 0, decoder.GetChars(bytes, 0, count, chars, 0, false));
                Screen.Feed(text);
                if (OutputReceived is { } handler)
                    foreach (Action<string> callback in handler.GetInvocationList())
                        try { callback(text); } catch (Exception) { /* 订阅者的异常不能中断输出读取，否则子进程会因缓冲区写满而阻塞。 */ }
            }
        }
        // Linux 在从设备全部关闭后读取主设备返回 EIO，macOS 返回 EOF；两者都表示输出结束。
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// 结束整个进程组并释放主设备。可重复调用。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            RefreshExitCode();
            _disposed = true;
            if (!_reaped)
            {
                UnixPtyNative.SendSignal(-ProcessId, UnixPtyNative.SignalHangup);
                var deadline = Environment.TickCount64 + HangupGraceMilliseconds;
                while (!_reaped && Environment.TickCount64 < deadline) { Thread.Sleep(20); RefreshExitCode(); }
                if (!_reaped)
                {
                    UnixPtyNative.SendSignal(-ProcessId, UnixPtyNative.SignalKill);
                    Reap(0);
                }
            }
            _input.Dispose();
        }
        if (Thread.CurrentThread != _reader) _reader.Join(DisposeWaitMilliseconds);
        _output.Dispose();
        Screen.ResponseRequested -= Reply;
    }
}
