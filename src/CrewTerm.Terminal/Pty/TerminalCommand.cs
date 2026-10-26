using System.ComponentModel;
using System.Runtime.Versioning;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 把用户输入的命令行解析为当前平台可以直接执行的形式。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>命令行开头的程序部分可以用双引号括起（路径含空格时必须如此），其余部分保持原样。</description></item>
/// <item><description>裸程序名（如 <c>claude</c>、<c>cmd.exe</c>、<c>bash</c>）只在 <c>PATH</c> 的绝对路径条目中查找，<b>不会</b>查找工作目录，
/// 防止打开不受信任的仓库时执行仓库中同名的程序或脚本（当前目录劫持）。</description></item>
/// <item><description>带目录的相对路径（如 <c>.\tools\run</c>、<c>./run.sh</c>）按工作目录解析；绝对路径只检查该位置。</description></item>
/// <item><description>Windows：无扩展名时依次尝试 <c>.exe/.com/.cmd/.bat</c>，优先于 npm 一并安装的无扩展名 Unix 脚本；
/// <c>.cmd/.bat</c> 通过系统目录中的 <c>cmd.exe /d /s /c</c> 启动，不读取 AutoRun。</description></item>
/// <item><description>Linux / macOS：只接受有执行权限的普通文件，经 <c>/bin/sh -c "exec …"</c> 执行，其余部分按 POSIX Shell 规则解释。</description></item>
/// </list>
/// </remarks>
internal static class TerminalCommand
{
    /// <summary>
    /// Win32 <c>ERROR_FILE_NOT_FOUND</c>（也是 POSIX <c>ENOENT</c>），与系统找不到程序时的错误码一致。
    /// </summary>
    private const int FileNotFound = 2;

    /// <summary>
    /// Windows 上程序名没有扩展名时依次尝试的扩展名；最后的空字符串表示原样匹配。
    /// </summary>
    private static readonly string[] SearchExtensions = [".exe", ".com", ".cmd", ".bat", ""];

    /// <summary>
    /// 解析命令行中的程序路径（Windows）。
    /// </summary>
    /// <param name="commandLine">用户命令行；程序路径含空格时须用双引号括起。</param>
    /// <param name="workingDirectory">会话工作目录（绝对路径），只用于解析带目录的相对路径。</param>
    /// <returns>以带引号的绝对程序路径开头的命令行；批处理脚本包装为 <c>cmd.exe</c> 调用。</returns>
    /// <exception cref="Win32Exception">找不到程序（错误码 2）。</exception>
    internal static string Resolve(string commandLine, string workingDirectory)
    {
        var (executable, remainder) = Split(commandLine);
        var extensions = Path.HasExtension(executable) ? [""] : SearchExtensions;
        foreach (var directory in CandidateDirectories(executable, workingDirectory))
        foreach (var extension in extensions)
        {
            if (Candidate(directory, executable + extension) is not { } candidate || !File.Exists(candidate)) continue;
            var resolved = $"\"{candidate}\"{remainder}";
            var kind = Path.GetExtension(candidate);
            if (kind.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || kind.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                return $"\"{shell}\" /d /s /c \"{resolved}\"";
            }
            return resolved;
        }
        throw new Win32Exception(FileNotFound, $"Program not found: {executable}");
    }

    /// <summary>
    /// 解析命令行中的程序路径（Linux / macOS），返回传给 <c>execve</c> 的参数列表。
    /// </summary>
    /// <param name="commandLine">用户命令行；程序路径含空格时须用双引号括起。</param>
    /// <param name="workingDirectory">会话工作目录（绝对路径），只用于解析带目录的相对路径。</param>
    /// <returns><c>["/bin/sh", "-c", "exec '程序绝对路径' 其余部分"]</c>。</returns>
    /// <exception cref="Win32Exception">找不到有执行权限的程序（错误码 2）。</exception>
    [UnsupportedOSPlatform("windows")]
    internal static string[] ResolveUnix(string commandLine, string workingDirectory)
    {
        var (executable, remainder) = Split(commandLine);
        foreach (var directory in CandidateDirectories(executable, workingDirectory))
        {
            if (Candidate(directory, executable) is not { } candidate || !IsExecutable(candidate)) continue;
            // exec 让 Shell 只负责解析其余部分的引号与参数，随后被目标程序替换：进程标识即程序本身，
            // Ctrl+C 等信号直接到达程序，而不会先结束一个居中的 Shell。
            return ["/bin/sh", "-c", "exec " + ShellQuote(candidate) + remainder];
        }
        throw new Win32Exception(FileNotFound, $"Program not found: {executable}");
    }

    /// <summary>
    /// 把命令行拆成程序部分与其余部分。
    /// </summary>
    /// <param name="commandLine">用户命令行。</param>
    /// <returns>去掉引号的程序部分，以及从程序部分之后开始、原样保留（含前导空白）的其余部分。</returns>
    /// <exception cref="Win32Exception">命令行没有程序部分。</exception>
    private static (string Executable, string Remainder) Split(string commandLine)
    {
        var command = commandLine.TrimStart();
        var quoted = command.StartsWith('"');
        var end = quoted ? command.IndexOf('"', 1) : command.IndexOfAny([' ', '\t']);
        if (end < 0) end = command.Length;
        var executable = quoted ? command[1..end] : command[..end];
        var remainder = command[Math.Min(end + (quoted ? 1 : 0), command.Length)..];
        if (string.IsNullOrWhiteSpace(executable)) throw new Win32Exception(FileNotFound, "The command does not name a program.");
        return (executable, remainder);
    }

    /// <summary>
    /// 组合目录与程序名并转换为绝对路径。
    /// </summary>
    /// <param name="directory">查找目录；空字符串表示程序名本身就是绝对路径。</param>
    /// <param name="name">程序名（可含扩展名）。</param>
    /// <returns>候选路径；路径非法时为 <see langword="null"/>。</returns>
    private static string? Candidate(string directory, string name)
    {
        try { return Path.GetFullPath(Path.Combine(directory, name)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// 判断路径是否是有执行权限（任一身份）的普通文件。
    /// </summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>可以执行时为 <see langword="true"/>。</returns>
    [UnsupportedOSPlatform("windows")]
    private static bool IsExecutable(string path)
        => File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>
    /// 用单引号为 POSIX Shell 引用一个值（单引号本身写为 <c>'\''</c>）。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>Shell 中按字面解释的引用文本。</returns>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// 列出查找程序的目录。
    /// </summary>
    /// <param name="executable">命令行中的程序部分。</param>
    /// <param name="workingDirectory">会话工作目录。</param>
    /// <returns>绝对路径返回单个空字符串（原样检查）；带目录的相对路径返回工作目录；裸程序名返回 <c>PATH</c> 中的绝对路径条目。</returns>
    private static IEnumerable<string> CandidateDirectories(string executable, string workingDirectory)
    {
        if (Path.IsPathRooted(executable)) return [""];
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return [workingDirectory];
        // PATH 中的相对条目（包括空条目，在 Unix 上表示当前目录）等同于在工作目录查找程序，直接忽略。
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0 && Path.IsPathFullyQualified(entry));
    }
}
