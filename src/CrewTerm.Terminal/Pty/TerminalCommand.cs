using System.ComponentModel;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 把用户输入的命令行解析为 <c>CreateProcess</c> 可直接执行的命令行。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>裸程序名（如 <c>claude</c>、<c>cmd.exe</c>）只在 <c>PATH</c> 中查找，<b>不会</b>查找工作目录，
/// 防止打开不受信任的仓库时执行仓库中同名的程序或脚本（当前目录劫持）。</description></item>
/// <item><description>带目录的相对路径（如 <c>.\tools\run</c>）按工作目录解析；绝对路径只检查该位置。</description></item>
/// <item><description>无扩展名时依次尝试 <c>.exe/.com/.cmd/.bat</c>，优先于 npm 一并安装的无扩展名 Unix 脚本。</description></item>
/// <item><description><c>.cmd/.bat</c> 通过系统目录中的 <c>cmd.exe /d /s /c</c> 启动，不读取 AutoRun。</description></item>
/// </list>
/// </remarks>
internal static class TerminalCommand
{
    /// <summary>
    /// Win32 <c>ERROR_FILE_NOT_FOUND</c>，与 <c>CreateProcess</c> 找不到程序时的错误码一致。
    /// </summary>
    private const int FileNotFound = 2;

    /// <summary>
    /// 程序名没有扩展名时依次尝试的扩展名；最后的空字符串表示原样匹配。
    /// </summary>
    private static readonly string[] SearchExtensions = [".exe", ".com", ".cmd", ".bat", ""];

    /// <summary>
    /// 解析命令行中的程序路径。
    /// </summary>
    /// <param name="commandLine">用户命令行；程序路径含空格时须用双引号括起。</param>
    /// <param name="workingDirectory">会话工作目录（绝对路径），只用于解析带目录的相对路径。</param>
    /// <returns>以带引号的绝对程序路径开头的命令行；批处理脚本包装为 <c>cmd.exe</c> 调用。</returns>
    /// <exception cref="Win32Exception">找不到程序（错误码 2）。</exception>
    internal static string Resolve(string commandLine, string workingDirectory)
    {
        var command = commandLine.TrimStart();
        var quoted = command.StartsWith('"');
        var end = quoted ? command.IndexOf('"', 1) : command.IndexOfAny([' ', '\t']);
        if (end < 0) end = command.Length;
        var executable = quoted ? command[1..end] : command[..end];
        var remainder = command[Math.Min(end + (quoted ? 1 : 0), command.Length)..];
        if (string.IsNullOrWhiteSpace(executable)) throw new Win32Exception(FileNotFound, "The command does not name a program.");
        var extensions = Path.HasExtension(executable) ? [""] : SearchExtensions;
        foreach (var directory in CandidateDirectories(executable, workingDirectory))
        foreach (var extension in extensions)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, executable + extension)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!File.Exists(candidate)) continue;
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
    /// 列出查找程序的目录。
    /// </summary>
    /// <param name="executable">命令行中的程序部分。</param>
    /// <param name="workingDirectory">会话工作目录。</param>
    /// <returns>绝对路径返回单个空字符串（原样检查）；带目录的相对路径返回工作目录；裸程序名返回 <c>PATH</c> 中的非空条目。</returns>
    private static IEnumerable<string> CandidateDirectories(string executable, string workingDirectory)
    {
        if (Path.IsPathRooted(executable)) return [""];
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return [workingDirectory];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0 && Path.IsPathFullyQualified(entry));
    }
}
