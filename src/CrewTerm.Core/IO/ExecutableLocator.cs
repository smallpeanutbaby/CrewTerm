namespace CrewTerm.Core.IO;

/// <summary>
/// 按 PATH（Windows 上还有 PATHEXT）规则查找可执行文件，行为与系统命令行解析一致。
/// </summary>
/// <remarks>
/// <para>Windows：npm 全局安装的 CLI 同时存在无扩展名的 Unix 脚本和 <c>.cmd</c> 启动脚本；
/// 对无扩展名的名称会先按 PATHEXT 尝试 <c>.COM/.EXE/.BAT/.CMD</c>，因此会找到可在 Windows 运行的版本。</para>
/// <para>Linux / macOS：只接受有执行权限（任一身份）的普通文件，与 Shell 的查找规则一致。</para>
/// <para>只查找 PATH 中的绝对路径条目，从不查找进程的当前目录：Agent 发现与 Git 调用都先用它解析出绝对路径再启动，
/// 防止在不受信任的项目目录中启动 CrewTerm 时，目录中的同名程序被系统的默认搜索顺序选中。</para>
/// <para>本类只检查文件，不会启动程序。</para>
/// </remarks>
public static class ExecutableLocator
{
    /// <summary>
    /// 查找可执行文件。
    /// </summary>
    /// <param name="executable">程序名（在 PATH 中搜索）或含目录的路径（只检查该位置）。</param>
    /// <param name="searchPath">替代 <c>PATH</c> 环境变量的搜索路径，主要用于测试。</param>
    /// <param name="pathExtensions">替代 <c>PATHEXT</c> 环境变量的扩展名列表，主要用于测试。</param>
    /// <returns>可执行文件的绝对路径；找不到时返回 <see langword="null"/>。</returns>
    public static string? Find(string executable, string? searchPath = null, string? pathExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var extensions = (pathExtensions ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasDirectory = executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || Path.IsPathRooted(executable);
        IEnumerable<string> directories = hasDirectory ? [""]
            : (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Trim('"'))
                // PATH 中的相对条目（如 “.”）会按当前目录解析，等同于在当前目录查找程序，存在劫持风险，直接忽略。
                .Where(item => item.Length > 0 && Path.IsPathFullyQualified(item));
        foreach (var directory in directories)
        {
            string candidate;
            // PATH 中可能包含非法路径字符的条目，跳过而不是让整个查找失败。
            try { candidate = Path.GetFullPath(Path.Combine(directory, executable)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { continue; }
            if (OperatingSystem.IsWindows() && !Path.HasExtension(candidate))
                foreach (var extension in extensions)
                    if (File.Exists(candidate + extension)) return candidate + extension.ToLowerInvariant();
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 判断候选路径是否可以执行：Windows 上只要文件存在；Linux / macOS 上还要求有执行权限。
    /// </summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>可以执行时为 <see langword="true"/>。</returns>
    private static bool IsExecutable(string path)
        => File.Exists(path) && (OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);
}
