namespace CrewTerm.Terminal;

internal static class TerminalCommand
{
    internal static string Resolve(string commandLine, string workingDirectory)
    {
        var command = commandLine.TrimStart();
        var quoted = command.StartsWith('"');
        var end = quoted ? command.IndexOf('"', 1) : command.IndexOfAny([' ', '\t']);
        if (end < 0) end = command.Length;
        var executable = quoted ? command[1..end] : command[..end];
        var remainder = command[Math.Min(end + (quoted ? 1 : 0), command.Length)..];
        // npm installs an extensionless Unix shell script beside the Windows .cmd launcher.
        // Prefer native Windows launchers even when the caller supplied an absolute base path.
        var extensions = Path.HasExtension(executable) ? new[] { "" } : new[] { ".exe", ".com", ".cmd", ".bat", "" };
        var directories = Path.IsPathRooted(executable) ? new[] { "" }
            : new[] { workingDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var directory in directories)
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory.Trim('"'), executable + extension);
            if (!File.Exists(candidate)) continue;
            var fullPath = Path.GetFullPath(candidate);
            var resolved = $"\"{fullPath}\"{remainder}";
            if (Path.GetExtension(fullPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(fullPath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                return $"\"{shell}\" /d /s /c \"{resolved}\"";
            }
            return resolved;
        }
        return commandLine;
    }
}
