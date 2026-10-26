namespace CrewTerm.Workspace;

public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory);
public sealed record SearchResult(string RelativePath, int LineNumber, string Text);

public static class FileWorkspace
{
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    public const int MaximumSearchResults = 500;
    internal static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", "bin", "obj", "node_modules", "artifacts", ".crewterm" };

    public static IReadOnlyList<FileEntry> ListDirectory(string root, string relativePath = "")
    {
        var normalized = WorkspacePath.Root(root);
        var directory = WorkspacePath.Resolve(normalized, relativePath);
        return new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0 && entry.Name != ".git")
            .Select(entry => new FileEntry(entry.Name, WorkspacePath.Relative(normalized, entry.FullName), (entry.Attributes & FileAttributes.Directory) != 0))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static DocumentBuffer Open(string root, string relativePath)
        => DocumentBuffer.Load(WorkspacePath.Root(root), relativePath);

    public static Task<IReadOnlyList<SearchResult>> SearchAsync(string root, string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return Task.Run<IReadOnlyList<SearchResult>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = WorkspacePath.Root(root);
            var results = new List<SearchResult>();
            foreach (var file in EnumerateSourceFiles(normalized, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DocumentBuffer buffer;
                try { buffer = Open(normalized, WorkspacePath.Relative(normalized, file)); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                using var reader = new StringReader(buffer.Text);
                var lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(new SearchResult(WorkspacePath.Relative(normalized, file), lineNumber, line));
                    if (results.Count >= MaximumSearchResults) return results;
                }
            }
            return results;
        }, cancellationToken);
    }

    internal static IEnumerable<string> EnumerateSourceFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectories.Contains(entry.Name)) pending.Push(entry.FullName);
                }
                else yield return entry.FullName;
            }
        }
    }
}
