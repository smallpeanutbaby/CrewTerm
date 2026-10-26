namespace CrewTerm.Testing.Fixtures;

/// <summary>
/// 计算路径解析全部符号链接之后的真实位置，用于与进程报告的当前目录比较。
/// </summary>
/// <remarks>
/// macOS 的临时目录 <c>/var/folders/...</c> 位于符号链接 <c>/var → /private/var</c> 之下，子进程用 <c>getcwd</c> 得到的是
/// <c>/private/var/...</c>；直接与测试中拼出的路径比较会误报不一致。
/// </remarks>
public static class CanonicalPath
{
    /// <summary>
    /// 解析路径中每一级目录的符号链接（Windows 上只转换为绝对路径）。
    /// </summary>
    /// <param name="path">已存在的路径。</param>
    /// <returns>不含符号链接的绝对路径。</returns>
    public static string Of(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return full;
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            // 逐级解析，链接目标本身也可能包含链接；限定次数，防止循环链接。
            for (var hops = 0; hops < 32 && new FileInfo(current).LinkTarget is { } target; hops++)
                current = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
        }
        return current;
    }
}
