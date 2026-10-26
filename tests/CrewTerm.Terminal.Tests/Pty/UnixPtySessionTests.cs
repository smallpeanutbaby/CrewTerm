using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.Terminal.Tests.Pty;

/// <summary>
/// 覆盖 <see cref="UnixPtySession"/> 的进程启动、环境变量、输入输出、窗口尺寸、退出状态、程序解析与释放行为。
/// 所有测试都通过真实的 <c>forkpty</c> 伪终端启动真实的 <c>/bin/sh</c> 或测试脚本，并轮询解析后的屏幕文本判断结果；
/// 需要文件的测试在系统临时目录下创建独立文件夹并在结束时删除。只在 Linux 与 macOS 上运行。
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class UnixPtySessionTests
{
    /// <summary>
    /// 验证：子进程看到 <c>TERM=xterm-256color</c> 与 <c>COLORTERM=truecolor</c>，而测试进程自身的这两个环境变量保持原值（只注入子进程）。
    /// </summary>
    [UnixFact]
    public async Task NativeSessionsAdvertiseTheirOwnTerminalCapabilities()
    {
        var parentTerm = Environment.GetEnvironmentVariable("TERM");
        using var session = UnixPtySession.Start("term", "/bin/sh -c 'echo \"terminal=$TERM color=$COLORTERM\"'", Path.GetTempPath(), 80, 24);
        await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("terminal=xterm-256color color=truecolor"));
        Assert.Equal(parentTerm, Environment.GetEnvironmentVariable("TERM"));
    }

    /// <summary>
    /// 验证：调用方追加的变量（含中文值）到达子进程，值为 <see langword="null"/> 的变量从子进程环境中删除。
    /// </summary>
    [UnixFact]
    public async Task EnvironmentOverridesAndRemovalsApplyOnlyToTheChild()
    {
        var environment = new Dictionary<string, string?> { ["CREWTERM_TEST_VALUE"] = "value-中文", ["HOME"] = null };
        using var session = UnixPtySession.Start("env", "/bin/sh -c 'echo \"v=$CREWTERM_TEST_VALUE\"; [ -z \"${HOME+set}\" ] && echo home-removed'",
            Path.GetTempPath(), 80, 24, environment);
        await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("home-removed"));
        Assert.Contains("v=value-中文", session.Screen.GetText());
        Assert.NotNull(Environment.GetEnvironmentVariable("HOME"));
    }

    /// <summary>
    /// 验证：空变量名、含 <c>=</c> 或 NUL 的变量名在启动进程前被拒绝。
    /// </summary>
    /// <param name="key">非法的变量名。</param>
    [UnixTheory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    public void InvalidEnvironmentKeysAreRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => UnixPtySession.Start("env-invalid", "/bin/sh -c 'exit 0'", Path.GetTempPath(), 80, 24,
            new Dictionary<string, string?> { [key] = "value" }));
    }

    /// <summary>
    /// 验证：真实命令的 UTF-8 输出出现在屏幕上，退出码 7 被如实记录，会话标识与启动时一致。
    /// </summary>
    [UnixFact]
    public async Task RealCommandProducesTerminalOutputAndExitCode()
    {
        using var session = UnixPtySession.Start("echo", "/bin/sh -c 'echo crewterm-native 中文; exit 7'", Path.GetTempPath(), 80, 24);
        try { await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("crewterm-native 中文")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY output timeout. Exited={session.HasExited}; code={session.ExitCode}; screen={session.Screen.GetText()}"); }
        Assert.Equal(7, session.ExitCode);
        Assert.Equal("echo", session.Id);
    }

    /// <summary>
    /// 验证：被信号结束的进程按 Shell 惯例报告为 128 加信号编号（<c>SIGTERM</c> 为 143）。
    /// </summary>
    [UnixFact]
    public async Task SignalTerminationIsReportedAs128PlusSignal()
    {
        using var session = UnixPtySession.Start("signal", "/bin/sh -c 'kill -TERM $$'", Path.GetTempPath(), 80, 24);
        await Poll.Until(() => session.HasExited);
        Assert.Equal(143, session.ExitCode);
    }

    /// <summary>
    /// 验证：命令经 <c>exec</c> 替换中间的 Shell，会话报告的进程标识就是目标程序本身。
    /// </summary>
    [UnixFact]
    public async Task ProcessIdIsTheLaunchedProgram()
    {
        using var session = UnixPtySession.Start("pid", "/bin/sh -c 'echo pid=$$; sleep 5'", Path.GetTempPath(), 80, 24);
        await Poll.Until(() => session.Screen.GetText().Contains("pid="));
        Assert.Contains("pid=" + session.ProcessId, session.Screen.GetText());
    }

    /// <summary>
    /// 验证：交互式 Shell 在调整为 100×30 后，<c>stty size</c> 读到内核中的新窗口尺寸（证明 <c>TIOCSWINSZ</c> 调用正确），
    /// 屏幕快照随之更新；写入 <c>exit 0</c> 后进程以退出码 0 结束，之后写入抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    [UnixFact]
    public async Task InteractiveInputAndResizeReachRealShell()
    {
        using var session = UnixPtySession.Start("interactive", "/bin/sh", Path.GetTempPath(), 80, 24);
        session.Resize(100, 30);
        session.Write("stty size\r");
        try { await Poll.Until(() => session.Screen.GetText().Contains("30 100")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY resize timeout. Exited={session.HasExited}; screen={session.Screen.GetText()}"); }
        Assert.Equal(100, session.Screen.Snapshot().Columns);
        Assert.Equal(30, session.Screen.Snapshot().Rows);
        session.Write("exit 0\r");
        await Poll.Until(() => session.HasExited);
        Assert.Equal(0, session.ExitCode);
        Assert.Throws<InvalidOperationException>(() => session.Write("echo late\r"));
    }

    /// <summary>
    /// 验证：释放会话会结束整个进程组（包括 Shell 在后台启动的子进程），可以重复调用，释放后写入抛出 <see cref="ObjectDisposedException"/>。
    /// </summary>
    [UnixFact]
    public async Task DisposeTerminatesTheProcessGroupAndIsIdempotent()
    {
        var root = CreateDirectory();
        try
        {
            var session = UnixPtySession.Start("dispose", "/bin/sh -c 'sleep 300 & echo $! > child.pid; wait'", root, 80, 24);
            var pidFile = Path.Combine(root, "child.pid");
            await Poll.Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
            var child = int.Parse(File.ReadAllText(pidFile).Trim());
            Assert.True(IsAlive(child));
            session.Dispose();
            session.Dispose();
            Assert.True(session.HasExited);
            await Poll.Until(() => !IsAlive(child) && !IsAlive(session.ProcessId));
            Assert.Throws<ObjectDisposedException>(() => session.Write("echo late\r"));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：一个会话的伪终端主设备不会被之后启动的另一个会话的进程继承（否则后者可以读写前者的终端）。
    /// Linux 上通过 <c>/proc/&lt;pid&gt;/fd</c> 确认第二个会话没有打开任何 <c>/dev/ptmx</c>；macOS 没有 <c>/proc</c>，只验证能正常启动。
    /// </summary>
    [UnixFact]
    public async Task MasterDevicesAreNotInheritedByOtherSessions()
    {
        using var first = UnixPtySession.Start("first", "/bin/sh", Path.GetTempPath(), 80, 24);
        using var second = UnixPtySession.Start("second",
            "/bin/sh -c 'if [ -d /proc/$$/fd ]; then ls -l /proc/$$/fd; else echo no-proc; fi; echo fd-listed'", Path.GetTempPath(), 200, 50);
        await Poll.Until(() => second.HasExited && second.Screen.GetText().Contains("fd-listed"));
        Assert.DoesNotContain("ptmx", second.Screen.GetText());
    }

    /// <summary>
    /// 验证：工作目录不存在时在启动进程前就抛出 <see cref="DirectoryNotFoundException"/>。
    /// </summary>
    [UnixFact]
    public void MissingWorkingDirectoryIsRejectedBeforeLaunching()
    {
        Assert.Throws<DirectoryNotFoundException>(() => UnixPtySession.Start("missing", "/bin/sh", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), 80, 24));
    }

    /// <summary>
    /// 验证：程序不存在时抛出 <see cref="Win32Exception"/>。
    /// </summary>
    [UnixFact]
    public void MissingExecutableIsReported()
    {
        Assert.Throws<Win32Exception>(() => UnixPtySession.Start("missing", "crewterm-does-not-exist-xyz", Path.GetTempPath(), 80, 24));
    }

    /// <summary>
    /// 验证：裸程序名不会在工作目录中查找——打开不受信任的仓库时，仓库中植入的同名可执行脚本不会运行。
    /// </summary>
    [UnixFact]
    public void BareNamesAreNotResolvedFromTheWorkingDirectory()
    {
        var root = CreateDirectory();
        try
        {
            WriteScript(Path.Combine(root, "crewterm-planted-tool"), "touch planted-ran\n");
            Assert.Throws<Win32Exception>(() => UnixPtySession.Start("planted", "crewterm-planted-tool", root, 80, 24));
            Assert.False(File.Exists(Path.Combine(root, "planted-ran")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：没有执行权限的文件不会被当作程序启动。
    /// </summary>
    [UnixFact]
    public void FilesWithoutExecutePermissionAreNotLaunched()
    {
        var root = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "not-executable"), "#!/bin/sh\necho ran\n");
            Assert.Throws<Win32Exception>(() => UnixPtySession.Start("noexec", "./not-executable", root, 80, 24));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：带目录的相对路径按工作目录解析；程序路径含空格与单引号时用双引号括起即可启动，其余参数按 POSIX Shell 规则拆分。
    /// </summary>
    /// <param name="relative"><see langword="true"/> 时以 <c>./</c> 相对路径启动，否则以带引号的绝对路径启动。</param>
    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProgramPathsAreQuotedAndArgumentsFollowShellRules(bool relative)
    {
        var root = CreateDirectory();
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(root, "it's tools")).FullName;
            WriteScript(Path.Combine(tools, "show args"), "printf '[%s]' \"$@\"; echo\n");
            var program = relative ? "./it's tools/show args" : Path.Combine(tools, "show args");
            using var session = UnixPtySession.Start("args", "\"" + program + "\" 'two words' plain \"double quoted\"", root, 80, 24);
            await Poll.Until(() => session.HasExited);
            Assert.Contains("[two words][plain][double quoted]", session.Screen.GetText());
            Assert.Equal(0, session.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 在系统临时目录下创建一个独立的测试目录。
    /// </summary>
    /// <returns>目录的完整路径。</returns>
    private static string CreateDirectory()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CrewTerm unix pty " + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// 写入一个带执行权限的 <c>/bin/sh</c> 脚本。
    /// </summary>
    /// <param name="path">脚本路径。</param>
    /// <param name="body">脚本正文（不含 <c>#!</c> 行）。</param>
    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// 判断进程是否仍在运行。
    /// </summary>
    /// <param name="pid">进程标识。</param>
    /// <returns>进程存在且未退出时为 <see langword="true"/>。</returns>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }
}
