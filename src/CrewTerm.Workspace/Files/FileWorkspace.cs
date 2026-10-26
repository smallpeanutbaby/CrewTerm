using CrewTerm.Workspace.Documents;

namespace CrewTerm.Workspace.Files;

/// <summary>
/// 以项目根目录为边界的文件浏览、打开与全文搜索。
/// </summary>
/// <remarks>
/// 所有路径都经过 <see cref="WorkspacePath"/> 校验；符号链接、目录联接与 <c>.git</c>
/// 永远不会出现在结果中。搜索与检查点会额外跳过构建产物等目录（见 <see cref="ExcludedDirectories"/>）。
/// </remarks>
public static class FileWorkspace
{
    /// <summary>
    /// 可在编辑器中打开、可被检查点捕获的单个文件大小上限（4 MiB）。
    /// </summary>
    public const int MaximumFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 一次全文搜索最多返回的结果数。
    /// </summary>
    public const int MaximumSearchResults = 500;

    /// <summary>
    /// 搜索与检查点遍历时跳过的目录名（不区分大小写）。文件区浏览不受此限制。
    /// </summary>
    internal static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", "bin", "obj", "node_modules", "artifacts", ".crewterm" };

    /// <summary>
    /// 列出项目内某个目录的直接子项，目录在前、按名称排序。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">要列出的目录，默认是根目录。</param>
    /// <returns>子项列表。</returns>
    public static IReadOnlyList<FileEntry> ListDirectory(string root, string relativePath = "")
    {
        var normalized = WorkspacePath.Root(root);
        var directory = WorkspacePath.Resolve(normalized, relativePath);
        return new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0 && !entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new FileEntry(entry.Name, WorkspacePath.Relative(normalized, entry.FullName), (entry.Attributes & FileAttributes.Directory) != 0))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 打开项目内的文本文件进行编辑。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">项目相对路径。</param>
    /// <returns>可编辑、可安全保存的文档。</returns>
    public static DocumentBuffer Open(string root, string relativePath)
        => DocumentBuffer.Load(WorkspacePath.Root(root), relativePath);

    /// <summary>
    /// 在后台线程逐行搜索项目文本文件（字面量、不区分大小写）。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="query">要查找的文本。</param>
    /// <param name="cancellationToken">取消搜索。</param>
    /// <returns>最多 <see cref="MaximumSearchResults"/> 条结果；无法解码的文件被跳过。</returns>
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
                // 复用编辑器的解码规则：二进制、过大或非 Unicode 文件直接跳过。
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

    /// <summary>
    /// 深度优先枚举项目内的文件（同级按名称排序），跳过链接、<c>.git</c> 与
    /// <see cref="ExcludedDirectories"/>；无权限的目录被忽略。
    /// </summary>
    /// <param name="root">已规范化的项目根目录。</param>
    /// <param name="cancellationToken">取消枚举。</param>
    /// <returns>文件绝对路径序列。</returns>
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
