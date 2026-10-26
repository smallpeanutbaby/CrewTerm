using System.Diagnostics;
using System.Runtime.Versioning;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.Terminal.Tests.Pty;

/// <summary>
/// 覆盖 <see cref="ConPtySession"/> 的进程启动、环境变量隔离、输入输出、尺寸调整、可执行文件解析与释放行为。
/// 所有测试都通过真实 ConPTY 伪控制台启动真实的 <c>cmd.exe</c>、<c>.cmd</c> 脚本或 PowerShell 进程，
/// 并轮询解析后的屏幕文本判断结果；需要文件的测试在系统临时目录下创建独立文件夹并在结束时删除。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySessionTests
{
    /// <summary>
    /// 验证：会话中的子进程看到 <c>TERM=xterm-256color</c> 与 <c>COLORTERM=truecolor</c>，
    /// 而测试进程自身的这两个环境变量保持原值不变（只注入子进程）。
    /// </summary>
    [WindowsFact]
    public async Task NativeSessionsAdvertiseTheirOwnTerminalCapabilities()
    {
        var parentTerm = Environment.GetEnvironmentVariable("TERM");
        var parentColor = Environment.GetEnvironmentVariable("COLORTERM");
        using var session = ConPtySession.Start("term-capabilities", "cmd.exe /d /q /c \"echo terminal=%TERM% color=%COLORTERM%\"",
            Path.GetTempPath(), 100, 24);
        await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("terminal=xterm-256color color=truecolor"));
        Assert.Contains("terminal=xterm-256color color=truecolor", session.Screen.GetText());
        Assert.Equal(parentTerm, Environment.GetEnvironmentVariable("TERM"));
        Assert.Equal(parentColor, Environment.GetEnvironmentVariable("COLORTERM"));
    }

    /// <summary>
    /// 验证：调用方显式传入的环境变量优先于终端默认值——<c>TERM</c> 被覆盖为 <c>vt100</c>，
    /// 值为 <see langword="null"/> 的 <c>COLORTERM</c> 在子进程中未定义。
    /// </summary>
    [WindowsFact]
    public async Task ExplicitEnvironmentOverridesTakePriorityOverTerminalDefaults()
    {
        using var session = ConPtySession.Start("term-overrides", "cmd.exe /d /q /c \"echo terminal=%TERM% & if defined COLORTERM (echo hascolor) else (echo nocolor)\"",
            Path.GetTempPath(), 100, 24, new Dictionary<string, string?> { ["TERM"] = "vt100", ["COLORTERM"] = null });
        await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("nocolor"));
        Assert.Contains("terminal=vt100", session.Screen.GetText());
        Assert.Contains("nocolor", session.Screen.GetText());
    }
    /// <summary>
    /// 验证：变量名含 <c>=</c> 或 NUL、为空，或变量值含 NUL（可借此在环境块中注入额外变量）时，
    /// <see cref="ConPtySession.Start"/> 抛出 <see cref="ArgumentException"/> 拒绝启动。
    /// </summary>
    /// <param name="key">待注入的环境变量名。</param>
    /// <param name="value">待注入的环境变量值。</param>
    [WindowsTheory]
    [InlineData("BAD=KEY", "value")]
    [InlineData("BAD\0KEY", "value")]
    [InlineData("BAD_KEY", "value\0INJECTED=bad")]
    [InlineData("", "value")]
    public void InvalidEnvironmentEntriesCannotInjectAdditionalVariables(string key, string value)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var session = ConPtySession.Start("env-invalid", "cmd.exe /d /q /c exit", Path.GetTempPath(), 80, 24,
                new Dictionary<string, string?> { [key] = value });
        });
    }
    /// <summary>
    /// 验证：两个并发会话为同一个随机变量名设置不同的中文值时，各自只输出自己的值、看不到对方的值，
    /// 且该变量始终没有出现在测试进程自身的环境中。
    /// </summary>
    [WindowsFact]
    public async Task SessionEnvironmentIsChildLocalAndIsolatedBetweenConcurrentSessions()
    {
        var key = "CREWTERM_ENV_" + Guid.NewGuid().ToString("N");
        Assert.Null(Environment.GetEnvironmentVariable(key));
        var command = $"cmd.exe /d /q /c \"echo value-%{key}%\"";
        using var first = ConPtySession.Start("env-a", command, Path.GetTempPath(), 80, 24,
            new Dictionary<string, string?> { [key] = "first-中文" });
        using var second = ConPtySession.Start("env-b", command, Path.GetTempPath(), 80, 24,
            new Dictionary<string, string?> { [key] = "second-中文" });
        await Poll.Until(() => first.HasExited && first.Screen.GetText().Contains("value-first-中文"));
        await Poll.Until(() => second.HasExited && second.Screen.GetText().Contains("value-second-中文"));
        Assert.DoesNotContain("second-中文", first.Screen.GetText());
        Assert.DoesNotContain("first-中文", second.Screen.GetText());
        Assert.Null(Environment.GetEnvironmentVariable(key));
    }

    /// <summary>
    /// 验证：把继承来的 <c>TEMP</c> 覆盖为 <see langword="null"/> 后，子进程中该变量未定义（输出 <c>removed</c>），
    /// 而测试进程自身的 <c>TEMP</c> 保持原值。
    /// </summary>
    [WindowsFact]
    public async Task NullEnvironmentOverrideRemovesInheritedVariableOnlyFromChild()
    {
        var parent = Environment.GetEnvironmentVariable("TEMP");
        Assert.False(string.IsNullOrEmpty(parent));
        using var session = ConPtySession.Start("env-remove", "cmd.exe /d /q /c \"if defined TEMP (echo inherited) else (echo removed)\"",
            Path.GetTempPath(), 80, 24, new Dictionary<string, string?> { ["TEMP"] = null });
        await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("removed"));
        Assert.Equal(parent, Environment.GetEnvironmentVariable("TEMP"));
    }

    /// <summary>
    /// 验证：真实命令的输出 <c>crewterm-native</c> 出现在会话屏幕上，进程退出码 7 被如实记录，会话标识与启动时传入的一致；
    /// 超时则带上退出状态与屏幕内容报告失败。
    /// </summary>
    [WindowsFact]
    public async Task RealCommandProducesTerminalOutputAndExitCode()
    {
        using var session = ConPtySession.Start("echo", "cmd.exe /d /q /c \"echo crewterm-native & exit /b 7\"", Path.GetTempPath(), 80, 24);
        try { await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("crewterm-native")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY output timeout. Exited={session.HasExited}; code={session.ExitCode}; screen={session.Screen.GetText()}"); }
        Assert.Equal(7, session.ExitCode);
        Assert.Equal("echo", session.Id);
    }

    /// <summary>
    /// 验证：交互式 <c>cmd.exe</c> 会话在调整为 100×30 后，写入的命令被真实 Shell 执行并回显到屏幕，
    /// 屏幕快照的列数与行数随之更新；随后写入 <c>exit 0</c> 使进程以退出码 0 结束。
    /// </summary>
    [WindowsFact]
    public async Task InteractiveInputAndResizeReachRealShell()
    {
        using var session = ConPtySession.Start("interactive", "cmd.exe /d /q", Path.GetTempPath(), 80, 24);
        session.Resize(100, 30);
        session.Write("echo crewterm-input\r");
        try { await Poll.Until(() => session.Screen.GetText().Contains("crewterm-input")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY input timeout. Exited={session.HasExited}; code={session.ExitCode}; screen={session.Screen.GetText()}"); }
        Assert.Equal(100, session.Screen.Snapshot().Columns);
        Assert.Equal(30, session.Screen.Snapshot().Rows);
        session.Write("exit 0\r");
        await Poll.Until(() => session.HasExited);
        Assert.Equal(0, session.ExitCode);
    }

    /// <summary>
    /// 验证：连续两次释放会话不会出错，会话拥有的 Shell 进程在 10 秒内退出、<see cref="ConPtySession.HasExited"/> 为
    /// <see langword="true"/>，释放后再写入输入抛出 <see cref="ObjectDisposedException"/>。
    /// </summary>
    [WindowsFact]
    public async Task DisposeTerminatesTheOwnedProcessAndIsIdempotent()
    {
        var session = ConPtySession.Start("dispose", "cmd.exe /d /q", Path.GetTempPath(), 80, 24);
        using var process = Process.GetProcessById(session.ProcessId);
        session.Dispose();
        session.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.HasExited);
        Assert.Throws<ObjectDisposedException>(() => session.Write("echo late\r"));
    }

    /// <summary>
    /// 验证：工作目录不存在时，<see cref="ConPtySession.Start"/> 在启动进程前就抛出 <see cref="DirectoryNotFoundException"/>。
    /// </summary>
    [WindowsFact]
    public void MissingWorkingDirectoryIsRejectedBeforeLaunching()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ConPtySession.Start("missing", "cmd.exe", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), 80, 24));
    }

    /// <summary>
    /// 验证：可执行文件不存在时，<see cref="ConPtySession.Start"/> 抛出 <see cref="System.ComponentModel.Win32Exception"/> 报告错误。
    /// </summary>
    [WindowsFact]
    public void MissingExecutableIsReported()
    {
        Assert.Throws<System.ComponentModel.Win32Exception>(() => ConPtySession.Start("missing", "crewterm-does-not-exist-xyz.exe", Path.GetTempPath(), 80, 24));
    }

    /// <summary>
    /// 验证：会话中的 cmd.exe 也不会在当前目录（项目目录）中查找程序——这正是 npm 启动脚本以裸名 <c>node</c> 调用
    /// Node.js 时被仓库中植入的 <c>node.bat</c> 劫持的途径；植入的脚本不运行，标记文件不出现。
    /// </summary>
    /// <returns>表示测试过程的任务。</returns>
    [WindowsFact]
    public async Task ShellInsideSessionDoesNotRunProgramsFromTheWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm cmd planted " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "planted-ran.txt");
            File.WriteAllText(Path.Combine(root, "crewterm-planted-node.bat"), $"@echo off\r\necho ran> \"{marker}\"\r\n");
            using var session = ConPtySession.Start("planted-cmd", "cmd.exe /d /c crewterm-planted-node", root, 80, 24);
            await Poll.Until(() => session.HasExited);
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：位于含空格目录、文件名也含空格的 <c>.cmd</c> 脚本，带一个含空格的带引号参数启动时，
    /// 能在伪控制台中正确运行：参数完整传入（输出 <c>shim-two words</c>），退出码 3 被如实记录。
    /// </summary>
    [WindowsFact]
    public async Task CmdShimWithSpacesAndArgumentsRunsInsidePseudoconsole()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var shim = Path.Combine(root, "agent shim.cmd");
            File.WriteAllText(shim, "@echo off\r\necho shim-%~1\r\nexit /b 3\r\n");
            using var session = ConPtySession.Start("shim", $"\"{shim}\" \"two words\"", root, 80, 24);
            await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("shim-two words"));
            Assert.Equal(3, session.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：以显式相对路径（<c>.\</c> 前缀）且不带扩展名启动命令时，会在工作目录中找到同名 <c>.cmd</c> 脚本并运行，退出码为 0。
    /// </summary>
    [WindowsFact]
    public async Task ExplicitRelativeCommandFindsCmdShimInWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "crewterm-test-shim.cmd"), "@echo off\r\necho shim-discovered\r\n");
            using var session = ConPtySession.Start("shim", @".\crewterm-test-shim", root, 80, 24);
            await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("shim-discovered"));
            Assert.Equal(0, session.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：不带路径的裸命令名不会解析到工作目录中被植入的同名 <c>.cmd</c> 脚本——启动抛出
    /// <see cref="System.ComponentModel.Win32Exception"/>，脚本本应写出的标记文件也不存在。
    /// </summary>
    [WindowsFact]
    public void BareCommandNeverRunsAProgramPlantedInTheWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm planted " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "planted-ran.txt");
            File.WriteAllText(Path.Combine(root, "crewterm-planted-tool.cmd"), $"@echo off\r\necho ran> \"{marker}\"\r\n");
            Assert.Throws<System.ComponentModel.Win32Exception>(() => ConPtySession.Start("planted", "crewterm-planted-tool", root, 80, 24));
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：同一目录中同时存在无扩展名的 Unix Shell 脚本（npm 安装时生成）与同名 <c>.cmd</c> 脚本时，
    /// 无论以绝对路径还是 <c>.\</c> 相对路径启动，实际运行的都是 <c>.cmd</c> 脚本：参数正确传入，退出码为 9，
    /// 屏幕上不出现 Unix 脚本的输出。
    /// </summary>
    /// <param name="absolutePath"><see langword="true"/> 时以带引号的绝对路径启动，否则以 <c>.\</c> 相对路径启动。</param>
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsCmdShimTakesPriorityOverExtensionlessUnixShim(bool absolutePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm npm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var extensionless = Path.Combine(root, "crewterm-test-agent");
            File.WriteAllText(extensionless, "#!/bin/sh\necho wrong-unix-shim\n");
            File.WriteAllText(extensionless + ".cmd", "@echo off\r\necho windows-shim-%~1\r\nexit /b 9\r\n");
            var command = absolutePath ? $"\"{extensionless}\" check" : @".\crewterm-test-agent check";
            using var session = ConPtySession.Start("shim-priority", command, root, 80, 24);
            await Poll.Until(() => session.HasExited && session.Screen.GetText().Contains("windows-shim-check"));
            Assert.Equal(9, session.ExitCode);
            Assert.DoesNotContain("wrong-unix-shim", session.Screen.GetText());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证：会话中的 PowerShell 以 <c>Start-Process</c> 启动一个脱离控制台的隐藏 <c>cmd.exe</c> 子进程（持续 <c>ping -t</c>）
    /// 并把其 PID 写入文件；释放会话后，这个后代进程也会在 10 秒内被终止。
    /// </summary>
    [WindowsFact]
    public async Task DisposeTerminatesDescendantProcessesOwnedByTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm child " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pidFile = Path.Combine(root, "child.pid");
            var script = "$child = Start-Process cmd.exe -ArgumentList '/d /q /c ping -t 127.0.0.1' -WindowStyle Hidden -PassThru; "
                + "$child.Id | Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "'; Start-Sleep -Seconds 120";
            using var session = ConPtySession.Start("children", $"powershell.exe -NoLogo -NoProfile -Command \"{script}\"", root, 80, 24);
            await Poll.Until(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            using var child = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile)).Trim()));
            try
            {
                Assert.False(child.HasExited);
                session.Dispose();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(child.HasExited);
            }
            finally
            {
                // 断言失败时也要结束 ping -t，否则它会一直留在系统中。
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
