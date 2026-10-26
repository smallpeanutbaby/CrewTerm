using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CrewTerm.Terminal.Emulation;
using CrewTerm.Terminal.Native;
using Microsoft.Win32.SafeHandles;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 持有一个 Windows 伪控制台（ConPTY）及其进程树，与任何 UI 无关。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>进程以挂起状态创建，先加入“关闭即结束”的作业对象再恢复运行，因此它启动的所有子进程都随会话结束。</description></item>
/// <item><description>后台读取线程持续读取输出并喂给 <see cref="Screen"/>；终端查询的回复在屏幕锁之外写回，避免与 resize 发生锁顺序反转。</description></item>
/// <item><description>写入、resize、退出状态查询与释放都由同一把锁串行化，可以从任意线程调用。</description></item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConPtySession : ITerminalSession
{
    /// <summary>
    /// 输出管道每次读取的字节数。
    /// </summary>
    private const int ReadBufferBytes = 16384;

    /// <summary>
    /// 释放时等待进程树退出、读取线程收尾的最长时间（毫秒）。
    /// </summary>
    private const int DisposeWaitMilliseconds = 2000;

    /// <summary>
    /// 保护原生句柄、写入与退出状态的锁。
    /// </summary>
    private readonly object _nativeGate = new();

    /// <summary>
    /// 写入伪控制台输入管道的流。
    /// </summary>
    private readonly FileStream _input;

    /// <summary>
    /// 读取伪控制台输出管道的流。
    /// </summary>
    private readonly FileStream _output;

    /// <summary>
    /// 持续读取输出的后台线程。
    /// </summary>
    private readonly Thread _reader;

    /// <summary>
    /// 伪控制台、进程与作业对象句柄；释放后为零。
    /// </summary>
    private IntPtr _console, _process, _job;

    /// <summary>
    /// 是否已释放。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 已观察到的进程退出码；进程仍在运行时为 <see langword="null"/>。
    /// </summary>
    private int? _exitCode;

    /// <summary>
    /// 会话标识。
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 用户提供的原始命令行（未经程序路径解析）。
    /// </summary>
    public string CommandLine { get; }

    /// <summary>
    /// 进程工作目录（绝对路径）。
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// 根进程的进程标识。
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// 由输出驱动的屏幕状态。
    /// </summary>
    public TerminalScreen Screen { get; }

    /// <summary>
    /// 每收到一段输出（已解码为文本、已送入 <see cref="Screen"/>）时在读取线程上触发。
    /// 回调抛出的异常会被吞掉，不会中断输出读取。
    /// </summary>
    public event Action<string>? OutputReceived;

    /// <summary>
    /// 进程是否已退出（会话已释放也视为已退出）。
    /// </summary>
    public bool HasExited
    {
        get { lock (_nativeGate) { RefreshExitCode(); return _disposed || _exitCode.HasValue; } }
    }

    /// <summary>
    /// 进程退出码；仍在运行或无法获知时为 <see langword="null"/>。
    /// </summary>
    public int? ExitCode
    {
        get { lock (_nativeGate) { RefreshExitCode(); return _exitCode; } }
    }

    /// <summary>
    /// 接管已创建的原生资源并启动输出读取线程。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="commandLine">原始命令行。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="columns">初始列数。</param>
    /// <param name="rows">初始行数。</param>
    /// <param name="console">伪控制台句柄（所有权转移给本对象）。</param>
    /// <param name="process">进程信息（进程句柄所有权转移给本对象）。</param>
    /// <param name="job">作业对象句柄（所有权转移给本对象）。</param>
    /// <param name="input">输入管道写端（所有权转移给本对象）。</param>
    /// <param name="output">输出管道读端（所有权转移给本对象）。</param>
    private ConPtySession(string id, string commandLine, string cwd, int columns, int rows, IntPtr console,
        ProcessInformation process, IntPtr job, IntPtr input, IntPtr output)
    {
        Id = id;
        CommandLine = commandLine;
        WorkingDirectory = cwd;
        ProcessId = process.ProcessId;
        Screen = new(columns, rows);
        _console = console;
        _process = process.Process;
        _job = job;
        _input = new(new SafeFileHandle(input, true), FileAccess.Write, 4096, false);
        _output = new(new SafeFileHandle(output, true), FileAccess.Read, ReadBufferBytes, false);
        Screen.ResponseRequested += Reply;
        _reader = new(ReadOutput) { IsBackground = true, Name = $"CrewTerm PTY {id}" };
        _reader.Start();
    }

    /// <summary>
    /// 在新的伪控制台中启动命令。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="commandLine">命令行；程序路径的解析规则见 <see cref="TerminalCommand"/>。</param>
    /// <param name="workingDirectory">工作目录，必须已存在。</param>
    /// <param name="columns">初始列数（1–500）。</param>
    /// <param name="rows">初始行数（1–300）。</param>
    /// <param name="environment">在继承的环境变量之上追加或覆盖的变量；值为 <see langword="null"/> 表示删除该变量。</param>
    /// <returns>已启动的会话。</returns>
    /// <exception cref="PlatformNotSupportedException">不是 Windows。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    /// <exception cref="Win32Exception">找不到程序或原生调用失败。</exception>
    public static ConPtySession Start(string id, string commandLine, string workingDirectory, int columns, int rows,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native terminal sessions currently require Windows ConPTY.");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        TerminalScreen.ValidateSize(columns, rows);
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        IntPtr inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, attributes = 0, job = 0, environmentBlock = 0;
        var process = new ProcessInformation();
        var attributesInitialized = false;
        try
        {
            environmentBlock = CreateEnvironmentBlock(environment);
            Check(ConPtyNative.CreatePipe(out inputRead, out inputWrite, 0, 0));
            Check(ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0));
            Marshal.ThrowExceptionForHR(ConPtyNative.CreatePseudoConsole(new((short)columns, (short)rows), inputRead, outputWrite, 0, out console));
            IntPtr size = 0;
            ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(ConPtyNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            Check(ConPtyNative.UpdateProcThreadAttribute(attributes, 0, ConPtyNative.ProcThreadAttributePseudoConsole, console, IntPtr.Size, 0, 0));
            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = ConPtyNative.StartUseStdHandles;
            Check(ConPtyNative.CreateProcess(null, new StringBuilder(TerminalCommand.Resolve(commandLine, cwd)), 0, 0, false,
                ConPtyNative.ExtendedStartupInfoPresent | ConPtyNative.CreateSuspended | ConPtyNative.CreateUnicodeEnvironment,
                environmentBlock, cwd, ref startup, out process));
            job = ConPtyNative.CreateJobObject(0, null);
            if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation();
            limits.Basic.LimitFlags = ConPtyNative.JobObjectLimitKillOnJobClose;
            Check(ConPtyNative.SetInformationJobObject(job, ConPtyNative.JobObjectExtendedLimitInformationClass, ref limits,
                Marshal.SizeOf<ExtendedLimitInformation>()));
            Check(ConPtyNative.AssignProcessToJobObject(job, process.Process));
            var session = new ConPtySession(id, commandLine, cwd, columns, rows, console, process, job, inputWrite, outputRead);
            // 所有权已转移给会话对象，finally 中不能再关闭这些句柄。
            console = job = inputWrite = outputRead = 0;
            process.Process = 0;
            if (ConPtyNative.ResumeThread(process.Thread) == ConPtyNative.ResumeThreadFailed)
            {
                session.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return session;
        }
        finally
        {
            if (environmentBlock != 0) Marshal.FreeHGlobal(environmentBlock);
            if (process.Process != 0) { ConPtyNative.TerminateProcess(process.Process, 1); ConPtyNative.CloseHandle(process.Process); }
            if (process.Thread != 0) ConPtyNative.CloseHandle(process.Thread);
            if (attributes != 0)
            {
                if (attributesInitialized) ConPtyNative.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (job != 0) ConPtyNative.CloseHandle(job);
            if (console != 0) ConPtyNative.ClosePseudoConsole(console);
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
                if (handle != 0) ConPtyNative.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 生成 UTF-16 环境块：继承当前进程的环境变量，声明本伪控制台的终端能力，再应用调用方的覆盖。
    /// </summary>
    /// <param name="overrides">追加、覆盖（非 <see langword="null"/> 值）或删除（<see langword="null"/> 值）的变量。</param>
    /// <returns>以 <see cref="Marshal.StringToHGlobalUni(string)"/> 分配的环境块，调用方负责释放。</returns>
    /// <exception cref="ArgumentException">变量名为空或包含 <c>=</c>、NUL，或变量值包含 NUL。</exception>
    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides)
    {
        // Windows 要求环境块按变量名（不区分大小写）排序。
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string)entry.Value!;
        // 描述的是这个伪控制台，而不是无界面 daemon 继承来的终端。
        values["TERM"] = "xterm-256color";
        values["COLORTERM"] = "truecolor";
        // 让会话中的 cmd.exe（包括 npm 安装的 codex.cmd、gemini.cmd 等启动脚本）不在当前目录中查找程序：
        // 这些脚本以裸名 “node” 调用 Node.js，而当前目录就是项目目录，打开不受信任的仓库时其中的 node.bat 会被执行。
        values["NoDefaultCurrentDirectoryInExePath"] = "1";
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (string.IsNullOrEmpty(key) || key.IndexOfAny(['=', '\0']) >= 0 || value?.Contains('\0') == true)
                throw new ArgumentException("Environment keys must be nonempty and cannot contain '=' or NUL; values cannot contain NUL.", nameof(overrides));
            if (value is null) values.Remove(key);
            else values[key] = value;
        }
        return Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => $"{pair.Key}={pair.Value}")) + "\0");
    }

    /// <summary>
    /// 向进程写入输入（按键编码、粘贴内容等）。
    /// </summary>
    /// <param name="text">要写入的文本；完整的旧式鼠标报告按原始字节发送，其余按 UTF-8 编码。</param>
    /// <exception cref="ObjectDisposedException">会话已释放。</exception>
    /// <exception cref="InvalidOperationException">进程已退出。</exception>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshExitCode();
            if (_exitCode.HasValue) throw new InvalidOperationException("The terminal process has exited.");
            _input.Write(Screen.EncodeInput(text));
            _input.Flush();
        }
    }

    /// <summary>
    /// 调整伪控制台与屏幕模型的尺寸。
    /// </summary>
    /// <param name="columns">列数（1–500）。</param>
    /// <param name="rows">行数（1–300）。</param>
    /// <exception cref="ObjectDisposedException">会话已释放。</exception>
    public void Resize(int columns, int rows)
    {
        TerminalScreen.ValidateSize(columns, rows);
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Marshal.ThrowExceptionForHR(ConPtyNative.ResizePseudoConsole(_console, new((short)columns, (short)rows)));
            Screen.Resize(columns, rows);
        }
    }

    /// <summary>
    /// 若进程已退出则记录其退出码。调用方必须持有 <see cref="_nativeGate"/>。
    /// </summary>
    private void RefreshExitCode()
    {
        if (_process != 0 && !_exitCode.HasValue && ConPtyNative.WaitForSingleObject(_process, 0) == ConPtyNative.WaitObject0
            && ConPtyNative.GetExitCodeProcess(_process, out var code)) _exitCode = unchecked((int)code);
    }

    /// <summary>
    /// 把终端查询的回复（光标位置、设备属性等）写回进程；进程已退出或会话已释放时忽略。
    /// </summary>
    /// <param name="text">回复序列。</param>
    private void Reply(string text)
    {
        try { Write(text); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>
    /// 读取线程主体：解码 UTF-8 输出（跨块保留不完整的字节序列），更新屏幕并通知订阅者，直到管道关闭。
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
                        try { callback(text); } catch (Exception) { /* 订阅者的异常不能中断输出读取，否则伪控制台会因管道写满而阻塞。 */ }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// 结束整个进程树并释放全部原生资源。可重复调用。
    /// </summary>
    public void Dispose()
    {
        IntPtr console;
        lock (_nativeGate)
        {
            if (_disposed) return;
            RefreshExitCode();
            _disposed = true;
            if (_job != 0) ConPtyNative.TerminateJobObject(_job, 1);
            if (_process != 0) { ConPtyNative.WaitForSingleObject(_process, DisposeWaitMilliseconds); RefreshExitCode(); }
            _input.Dispose();
            console = _console;
            _console = 0;
        }
        // 关闭伪控制台时它会刷新最后的屏幕状态，读取线程必须继续读取，因此在锁外关闭并等待读取线程。
        if (console != 0) ConPtyNative.ClosePseudoConsole(console);
        if (Thread.CurrentThread != _reader) _reader.Join(DisposeWaitMilliseconds);
        _output.Dispose();
        lock (_nativeGate)
        {
            if (_process != 0) ConPtyNative.CloseHandle(_process);
            if (_job != 0) ConPtyNative.CloseHandle(_job);
            _process = _job = 0;
        }
        Screen.ResponseRequested -= Reply;
    }

    /// <summary>
    /// 原生调用失败时抛出带最后错误码的 <see cref="Win32Exception"/>。
    /// </summary>
    /// <param name="result">原生调用的返回值。</param>
    private static void Check(bool result)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
