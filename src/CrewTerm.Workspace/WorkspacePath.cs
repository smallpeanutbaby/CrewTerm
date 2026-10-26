namespace CrewTerm.Workspace;

internal static class WorkspacePath
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Root(string root)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Project directory does not exist: {full}");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Symbolic-link project roots are not supported. Open the target directory explicitly.");
        return full;
    }

    internal static string Resolve(string root, string relativePath, bool allowMissing = false)
    {
        var fullRoot = Root(root);
        if (System.IO.Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Use a project-relative path.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relativePath));
        var prefix = System.IO.Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + System.IO.Path.DirectorySeparatorChar;
        if (!full.Equals(fullRoot, Comparison) && !full.StartsWith(prefix, Comparison))
            throw new UnauthorizedAccessException("The path is outside the selected project.");
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

    internal static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}
