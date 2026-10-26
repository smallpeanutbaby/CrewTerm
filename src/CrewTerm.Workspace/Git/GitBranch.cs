using CrewTerm.Core.Security;

namespace CrewTerm.Workspace.Git;

/// <summary>
/// 读取目录所在 Git 仓库当前检出的分支名，用于侧栏中每个空间旁边的暗色说明。
/// </summary>
/// <remarks>
/// <para>直接读取 <c>.git/HEAD</c> 而不启动 <c>git</c> 进程，开销只是向上查找 <c>.git</c> 与一次小文件读取。
/// 支持普通仓库与 worktree / 子模块（<c>.git</c> 为写有 <c>gitdir:</c> 的文件）。即便如此，网络路径上的文件访问仍可能长时间阻塞，
/// 调用方不应在界面线程上调用 <see cref="Read"/>。</para>
/// <para>仓库内容不受信任：</para>
/// <list type="bullet">
/// <item><description><c>gitdir:</c> 指向网络路径（<c>\\server\share</c>、<c>//server/share</c>）或设备路径（<c>\\?\</c>、<c>\\.\</c>、<c>\??\</c>）时不跟随，
/// 避免解压的不可信目录让工作台连接攻击者的共享（在 Windows 上会发送 NTLM 凭据）。</description></item>
/// <item><description>只读取长度大于 0 的普通文件：命名管道（FIFO）与字符设备的长度为 0，打开它们可能永远阻塞。</description></item>
/// <item><description>返回值去掉控制字符并限制长度后才可显示。</description></item>
/// </list>
/// </remarks>
public static class GitBranch
{
    /// <summary>
    /// 读取 <c>HEAD</c> 或 <c>.git</c> 文件的最大字节数；正常内容只有几十字节。
    /// </summary>
    private const int MaxHeadBytes = 1024;

    /// <summary>
    /// 返回的分支名的最大字符数。
    /// </summary>
    private const int MaxNameLength = 64;

    /// <summary>
    /// 从目录开始向上查找 Git 仓库，返回当前分支名。
    /// </summary>
    /// <remarks>
    /// 找到的第一个 <c>.git</c> 决定结果：它是格式错误或指向被拒绝位置的 <c>.git</c> 文件时返回 <see langword="null"/>，
    /// 不会继续向上把外层仓库的分支误报为本目录的分支。
    /// </remarks>
    /// <param name="directory">项目目录（可以位于仓库的子目录中）。</param>
    /// <returns>分支名（例如 <c>main</c>、<c>feature/login</c>）；分离 HEAD 时为提交哈希的前 7 位；不在仓库中或无法读取时为 <see langword="null"/>。</returns>
    public static string? Read(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(dotGit)) return Parse(ReadSmallFile(Path.Combine(dotGit, "HEAD")));
                if (!File.Exists(dotGit)) continue;
                return LinkedDirectory(dotGit, current.FullName) is { } linked ? Parse(ReadSmallFile(Path.Combine(linked, "HEAD"))) : null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// 解析 <c>HEAD</c> 文件内容。
    /// </summary>
    /// <remarks>
    /// 引用名中任何一段以 <c>.</c> 开头都视为无法识别（Git 不允许这样的分支名）；reftable 格式的仓库在 <c>HEAD</c> 中写的正是
    /// <c>ref: refs/heads/.invalid</c>，真实分支存放在 reftable 中，这里不读取，因此不显示分支。
    /// </remarks>
    /// <param name="head">文件内容；为 <see langword="null"/> 表示文件不存在或不可读取。</param>
    /// <returns>分支名、提交哈希前 7 位，或无法识别时为 <see langword="null"/>。</returns>
    public static string? Parse(string? head)
    {
        var line = head?.Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(line)) return null;
        const string branchPrefix = "ref: refs/heads/";
        var name = line.StartsWith(branchPrefix, StringComparison.Ordinal) ? line[branchPrefix.Length..]
            : line.StartsWith("ref: ", StringComparison.Ordinal) ? line[5..]
            : line.Length >= 7 && line.All(Uri.IsHexDigit) ? line[..7] : null;
        if (string.IsNullOrWhiteSpace(name) || name.Split('/').Any(part => part.StartsWith('.'))) return null;
        name = DisplayText.Sanitize(name).Trim();
        return name.Length == 0 ? null : name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    /// <summary>
    /// 判断 <c>gitdir:</c> 的目标是否为网络路径或设备路径（这类目标不跟随）。
    /// </summary>
    /// <param name="target"><c>gitdir:</c> 之后的原始文本。</param>
    /// <returns>以 <c>\\</c>、<c>//</c>、<c>\/</c>、<c>/\</c> 或 <c>\??\</c> 开头时为 <see langword="true"/>。</returns>
    public static bool IsRemoteOrDevicePath(string target) =>
        target.Length >= 2 && target[0] is '\\' or '/' && target[1] is '\\' or '/'
        || target.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>
    /// 读取 <c>.git</c> 文件中 <c>gitdir:</c> 指向的目录（worktree 与子模块使用这种形式）。
    /// </summary>
    /// <param name="file"><c>.git</c> 文件。</param>
    /// <param name="baseDirectory">相对路径的基准目录（<c>.git</c> 文件所在目录）。</param>
    /// <returns>Git 目录的绝对路径；格式不对或目标是网络、设备路径时为 <see langword="null"/>。</returns>
    private static string? LinkedDirectory(string file, string baseDirectory)
    {
        var line = ReadSmallFile(file)?.Split('\n', 2)[0].Trim();
        if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        var target = line[7..].Trim();
        if (target.Length == 0 || IsRemoteOrDevicePath(target)) return null;
        var full = Path.GetFullPath(target, baseDirectory);
        // 相对路径只能在项目自身所在的卷或共享上解析；解析结果变成另一处网络路径时同样拒绝。
        return IsRemoteOrDevicePath(full) && !IsRemoteOrDevicePath(baseDirectory) ? null : full;
    }

    /// <summary>
    /// 读取一个小的普通文件的开头部分（最多 <see cref="MaxHeadBytes"/> 字节）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容；文件不存在、长度为 0（包括命名管道与设备）或是目录时为 <see langword="null"/>。</returns>
    private static string? ReadSmallFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxHeadBytes];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, count);
    }
}
