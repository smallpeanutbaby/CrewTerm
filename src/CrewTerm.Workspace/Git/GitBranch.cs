using CrewTerm.Core.Security;

namespace CrewTerm.Workspace.Git;

/// <summary>
/// 读取目录所在 Git 仓库当前检出的分支名，用于侧栏中每个空间旁边的暗色说明。
/// </summary>
/// <remarks>
/// 直接读取 <c>.git/HEAD</c> 而不启动 <c>git</c> 进程：每次界面刷新都可以调用，开销只是一次小文件读取。
/// 支持普通仓库与 worktree / 子模块（<c>.git</c> 为写有 <c>gitdir:</c> 的文件）。仓库内容不受信任，
/// 返回值去掉控制字符并限制长度后才可显示。
/// </remarks>
public static class GitBranch
{
    /// <summary>
    /// 读取 <c>HEAD</c> 文件的最大字节数；正常内容只有几十字节。
    /// </summary>
    private const int MaxHeadBytes = 1024;

    /// <summary>
    /// 返回的分支名的最大字符数。
    /// </summary>
    private const int MaxNameLength = 64;

    /// <summary>
    /// 从目录开始向上查找 Git 仓库，返回当前分支名。
    /// </summary>
    /// <param name="directory">项目目录（可以位于仓库的子目录中）。</param>
    /// <returns>分支名（例如 <c>main</c>、<c>feature/login</c>）；分离 HEAD 时为提交哈希的前 7 位；不在仓库中或无法读取时为 <see langword="null"/>。</returns>
    public static string? Read(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                var gitDirectory = Directory.Exists(dotGit) ? dotGit : File.Exists(dotGit) ? LinkedDirectory(dotGit, current.FullName) : null;
                if (gitDirectory is not null) return Parse(ReadHead(Path.Combine(gitDirectory, "HEAD")));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// 解析 <c>HEAD</c> 文件内容。
    /// </summary>
    /// <param name="head">文件内容；为 <see langword="null"/> 表示文件不存在。</param>
    /// <returns>分支名、提交哈希前 7 位，或无法识别时为 <see langword="null"/>。</returns>
    public static string? Parse(string? head)
    {
        var line = head?.Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(line)) return null;
        const string branchPrefix = "ref: refs/heads/";
        var name = line.StartsWith(branchPrefix, StringComparison.Ordinal) ? line[branchPrefix.Length..]
            : line.StartsWith("ref: ", StringComparison.Ordinal) ? line[5..]
            : line.Length >= 7 && line.All(Uri.IsHexDigit) ? line[..7] : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = DisplayText.Sanitize(name).Trim();
        return name.Length == 0 ? null : name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    /// <summary>
    /// 读取 <c>.git</c> 文件中 <c>gitdir:</c> 指向的目录（worktree 与子模块使用这种形式）。
    /// </summary>
    /// <param name="file"><c>.git</c> 文件。</param>
    /// <param name="baseDirectory">相对路径的基准目录（<c>.git</c> 文件所在目录）。</param>
    /// <returns>Git 目录的绝对路径；格式不对时为 <see langword="null"/>。</returns>
    private static string? LinkedDirectory(string file, string baseDirectory)
    {
        var line = ReadHead(file)?.Split('\n', 2)[0].Trim();
        if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        var target = line[7..].Trim();
        return target.Length == 0 ? null : Path.GetFullPath(target, baseDirectory);
    }

    /// <summary>
    /// 读取小文件的开头部分（最多 <see cref="MaxHeadBytes"/> 字节）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容；文件不存在时为 <see langword="null"/>。</returns>
    private static string? ReadHead(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxHeadBytes];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, count);
    }
}
