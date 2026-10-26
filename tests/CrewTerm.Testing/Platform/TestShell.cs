namespace CrewTerm.Testing.Platform;

/// <summary>
/// 测试中使用的、按当前平台选择的 Shell 命令与伪造程序。
/// </summary>
/// <remarks>
/// <para>Windows 使用 <c>cmd.exe</c>（<c>/d</c> 不读取 AutoRun，<c>/q</c> 关闭命令回显），Linux / macOS 使用 <c>/bin/sh</c>。</para>
/// <para>伪造程序在 Windows 上是 <c>名称.cmd</c>，在 Linux / macOS 上是无扩展名、带执行权限的 <c>#!/bin/sh</c> 脚本；
/// 两者都能被 PATH 查找与 CrewTerm 的 Provider 识别（文件名不含扩展名的部分即 Provider 名）。</para>
/// <para>各方法只接受简单文本（字母、数字、空格、连字符、下划线、点），不做 Shell 转义；这是测试夹具，不处理任意输入。</para>
/// </remarks>
public static class TestShell
{
    /// <summary>
    /// 伪造程序保持运行的秒数，足以覆盖任何单个测试。
    /// </summary>
    private const int IdleSeconds = 60;

    /// <summary>
    /// 一直运行、等待输入的交互式 Shell 命令，用作“正在运行的终端”。
    /// </summary>
    public static string Interactive => OperatingSystem.IsWindows() ? "cmd.exe /d /q" : "/bin/sh";

    /// <summary>
    /// CrewTerm 可执行文件的文件名：Windows 为 <c>crewterm.exe</c>，其他平台为 <c>crewterm</c>。
    /// </summary>
    public static string CrewTermFileName => OperatingSystem.IsWindows() ? "crewterm.exe" : "crewterm";

    /// <summary>
    /// 立即以指定退出码结束的命令。
    /// </summary>
    /// <param name="code">退出码（0–255）。</param>
    /// <returns>命令行。</returns>
    public static string Exit(int code) => OperatingSystem.IsWindows() ? $"cmd.exe /d /c exit /b {code}" : $"/bin/sh -c 'exit {code}'";

    /// <summary>
    /// 输出一行文本后退出的命令。
    /// </summary>
    /// <param name="text">要输出的简单文本。</param>
    /// <returns>命令行。</returns>
    public static string Echo(string text) => OperatingSystem.IsWindows() ? $"cmd.exe /d /q /c \"echo {text}\"" : $"/bin/sh -c 'echo {text}'";

    /// <summary>
    /// 后台任务命令（Windows 经 <c>cmd.exe /c</c>、Linux / macOS 经 <c>/bin/sh -c</c> 执行）中等待指定秒数的一步。
    /// </summary>
    /// <param name="seconds">等待秒数。</param>
    /// <returns>命令片段；Windows 用 <c>ping</c> 计时（每次间隔约 1 秒），因为 <c>timeout</c> 在没有控制台时会立即失败。</returns>
    public static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

    /// <summary>
    /// 后台任务命令中以指定退出码结束 Shell 的一步。
    /// </summary>
    /// <param name="code">退出码（0–255）。</param>
    /// <returns>命令片段：Windows 为 <c>exit /b N</c>（只结束批处理上下文时也保留退出码），Linux / macOS 为 <c>exit N</c>。</returns>
    public static string ExitStep(int code) => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    /// <summary>
    /// 在交互式 Shell（<see cref="Interactive"/>）中输入的一行命令：依次输出 <c>前缀1</c> 到 <c>前缀N</c>，每个一行。
    /// </summary>
    /// <param name="prefix">每行的前缀，例如 <c>HISTORY_LINE_</c>。</param>
    /// <param name="count">行数。</param>
    /// <returns>不含回车的命令行；输入的命令本身不包含任何完整的输出行（例如 <c>前缀N</c>）。</returns>
    public static string CountLines(string prefix, int count) => OperatingSystem.IsWindows()
        ? $"for /l %i in (1,1,{count}) do @echo {prefix}%i"
        : $"i=1; while [ $i -le {count} ]; do echo {prefix}$i; i=$((i+1)); done";

    /// <summary>
    /// 把多步命令按当前平台的 Shell 语法顺序连接（Windows 用 <c>&amp;</c>，Linux / macOS 用 <c>;</c>），前一步失败不影响后续步骤。
    /// </summary>
    /// <param name="steps">各步命令，例如 <c>echo started</c>、<see cref="Sleep"/> 的结果。</param>
    /// <returns>可作为后台任务命令的单行文本。</returns>
    public static string Sequence(params string[] steps) => string.Join(OperatingSystem.IsWindows() ? " & " : "; ", steps);

    /// <summary>
    /// 把环境变量的值写入工作目录中的文件后退出的命令。
    /// </summary>
    /// <param name="variable">环境变量名。</param>
    /// <param name="file">文件名（相对工作目录）。</param>
    /// <returns>命令行；文件内容为变量值，末尾可能带空白与换行，读取时应修剪。</returns>
    public static string WriteVariable(string variable, string file)
        => OperatingSystem.IsWindows() ? $"cmd.exe /d /c echo %{variable}% > {file}" : $"/bin/sh -c 'printf %s \"${variable}\" > {file}'";

    /// <summary>
    /// 创建一个包装脚本：运行它等同于运行 <paramref name="commandLine"/> 并追加全部参数。
    /// </summary>
    /// <param name="directory">脚本所在目录（必须已存在）。</param>
    /// <param name="name">不含扩展名的脚本名，例如 <c>codex</c>。</param>
    /// <param name="commandLine">被包装的命令行，其中的路径须已用双引号括起。</param>
    /// <returns>脚本的完整路径。</returns>
    public static string WriteLauncher(string directory, string name, string commandLine)
        => WriteProgram(directory, name, "@echo off\r\n" + commandLine + " %*\r\n", "#!/bin/sh\nexec " + commandLine + " \"$@\"\n");

    /// <summary>
    /// 创建一个什么也不做、只保持运行的伪造程序（模拟正在交互的 Provider CLI）。
    /// </summary>
    /// <param name="directory">程序所在目录（必须已存在）。</param>
    /// <param name="name">不含扩展名的程序名，例如 <c>claude</c>。</param>
    /// <returns>程序的完整路径。</returns>
    public static string WriteIdleProgram(string directory, string name)
        => WriteProgram(directory, name, $"@echo off\r\nping -n {IdleSeconds} 127.0.0.1 >nul\r\n", $"#!/bin/sh\nsleep {IdleSeconds}\n");

    /// <summary>
    /// 创建一个伪造的 Provider CLI：以 <c>resume &lt;标识&gt;</c> 启动时把标识写入工作目录中的文件，随后保持运行。
    /// </summary>
    /// <param name="directory">程序所在目录（必须已存在）。</param>
    /// <param name="name">不含扩展名的程序名，例如 <c>codex</c>。</param>
    /// <param name="file">记录标识的文件名（相对会话工作目录）。</param>
    /// <returns>程序的完整路径。</returns>
    public static string WriteResumeRecorder(string directory, string name, string file)
        => WriteProgram(directory, name,
            $"@echo off\r\nif \"%~1\"==\"resume\" (\r\n echo %~2>{file}\r\n)\r\nping -n {IdleSeconds} 127.0.0.1 >nul\r\n",
            $"#!/bin/sh\nif [ \"$1\" = resume ]; then printf '%s\\n' \"$2\" > {file}; fi\nsleep {IdleSeconds}\n");

    /// <summary>
    /// 创建一个伪造程序：把收到的全部参数以空格连接写入工作目录中的文件后退出。
    /// </summary>
    /// <param name="directory">程序所在目录（必须已存在）。</param>
    /// <param name="name">不含扩展名的程序名，例如 <c>codex</c>。</param>
    /// <param name="file">记录参数的文件名（相对工作目录）；内容末尾可能带空白与换行，读取时应修剪。</param>
    /// <returns>程序的完整路径。</returns>
    public static string WriteArgumentRecorder(string directory, string name, string file)
        => WriteProgram(directory, name, $"@echo off\r\necho %* > {file}\r\n", $"#!/bin/sh\necho \"$@\" > {file}\n");

    /// <summary>
    /// 在 <c>PATH</c> 的绝对路径条目中查找程序。
    /// </summary>
    /// <param name="name">不含扩展名的程序名；Windows 上依次尝试 <c>.exe</c> 与 <c>.cmd</c>。</param>
    /// <returns>程序的完整路径；找不到时为 <see langword="null"/>。</returns>
    public static string? Find(string name)
    {
        string[] names = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd"] : [name];
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var candidate in names.Select(file => Path.Combine(directory, file)))
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 按当前平台写入伪造程序：Windows 写 <c>名称.cmd</c>，Linux / macOS 写带执行权限的无扩展名脚本。
    /// </summary>
    /// <param name="directory">程序所在目录（必须已存在）。</param>
    /// <param name="name">不含扩展名的程序名。</param>
    /// <param name="windows">批处理脚本内容（CRLF 换行）。</param>
    /// <param name="unix">以 <c>#!/bin/sh</c> 开头的脚本内容（LF 换行）。</param>
    /// <returns>程序的完整路径。</returns>
    private static string WriteProgram(string directory, string name, string windows, string unix)
    {
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(batch, windows);
            return batch;
        }
        var script = Path.Combine(directory, name);
        File.WriteAllText(script, unix);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return script;
    }
}
