namespace CrewTerm.Automation;

public static class ExecutableLocator
{
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
                .Select(item => item.Trim('"'));
        foreach (var directory in directories)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, executable)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { continue; }
            if (OperatingSystem.IsWindows() && !Path.HasExtension(candidate))
                foreach (var extension in extensions)
                    if (File.Exists(candidate + extension)) return candidate + extension.ToLowerInvariant();
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
