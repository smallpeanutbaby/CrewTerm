using CrewTerm.Core.IO;

namespace CrewTerm.Workspace.Files;

/// <summary>
/// 项目根目录内的路径解析与越界防护。
/// </summary>
/// <remarks>
/// 文件区、编辑器、Git 与检查点的所有路径都经过这里：拒绝绝对路径、<c>..</c> 越界，
/// 以及路径上任何一级是符号链接或目录联接（junction），防止通过链接读写项目外的文件。
/// </remarks>
internal static class WorkspacePath
{
    /// <summary>
    /// 规范化并校验项目根目录。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <returns>去掉末尾分隔符的绝对路径。</returns>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    /// <exception cref="UnauthorizedAccessException">根目录本身是符号链接。</exception>
    internal static string Root(string root)
    {
        var full = FileSystemPath.Normalize(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Project directory does not exist: {full}");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Symbolic-link project roots are not supported. Open the target directory explicitly.");
        return full;
    }

    /// <summary>
    /// 把项目相对路径解析为绝对路径，并逐级检查不越界、不经过链接。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">相对路径，允许 <c>/</c> 或 <c>\</c> 分隔。</param>
    /// <param name="allowMissing">为 <see langword="true"/> 时允许路径（或其上级）尚不存在，用于新建或恢复文件。</param>
    /// <returns>位于项目内的绝对路径。</returns>
    /// <exception cref="UnauthorizedAccessException">绝对路径、越界或经过符号链接。</exception>
    /// <exception cref="FileNotFoundException">路径不存在且 <paramref name="allowMissing"/> 为 <see langword="false"/>。</exception>
    internal static string Resolve(string root, string relativePath, bool allowMissing = false)
    {
        var fullRoot = Root(root);
        if (System.IO.Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Use a project-relative path.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relativePath));
        if (!FileSystemPath.IsWithin(fullRoot, full))
            throw new UnauthorizedAccessException("The path is outside the selected project.");
        // 逐级检查：即使最终路径在根目录内，中间某级若是 junction 也可能指向项目外部。
        var current = fullRoot;
        foreach (var component in System.IO.Path.GetRelativePath(fullRoot, full).Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            current = System.IO.Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (allowMissing) continue;
                throw new FileNotFoundException("Project path does not exist.", current);
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links and junctions are excluded from the project file workspace.");
        }
        return full;
    }

    /// <summary>
    /// 计算相对路径，并统一使用 <c>/</c> 作为分隔符（与 Git 输出一致）。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="path">项目内的绝对路径。</param>
    /// <returns>以 <c>/</c> 分隔的相对路径。</returns>
    internal static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}
