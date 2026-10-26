namespace CrewTerm.Core.IO;

/// <summary>
/// 统一的文件系统路径比较与规范化规则。
/// </summary>
/// <remarks>
/// Windows 文件系统默认不区分大小写，其他平台区分大小写。项目、会话、检查点和配置文件
/// 都按此规则判断“是否为同一路径”，避免各层各写一套比较逻辑而产生不一致。
/// </remarks>
public static class FileSystemPath
{
    /// <summary>
    /// 当前平台的路径字符串比较方式。
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 当前平台的路径比较器，可用于字典、集合和 LINQ。
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// 判断两个路径字符串是否指向同一位置（仅比较文本，不解析符号链接）。
    /// </summary>
    /// <param name="left">第一个路径；可以为 <see langword="null"/>。</param>
    /// <param name="right">第二个路径；可以为 <see langword="null"/>。</param>
    /// <returns>两者按平台规则相等时返回 <see langword="true"/>。</returns>
    public static bool AreSame(string? left, string? right) => string.Equals(left, right, Comparison);

    /// <summary>
    /// 转换为绝对路径并去掉末尾的目录分隔符（根目录如 <c>C:\</c> 保持不变）。
    /// </summary>
    /// <param name="path">相对或绝对路径。</param>
    /// <returns>可直接用于比较和持久化的规范化路径。</returns>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// 规范化路径，并在 Linux / macOS 上逐级解析其中已存在的符号链接，得到外部程序（例如 Git）报告的真实位置。
    /// </summary>
    /// <remarks>
    /// macOS 的临时目录 <c>/var/folders/...</c> 位于符号链接 <c>/var → /private/var</c> 之下，Git 输出的是 <c>/private/var/...</c>；
    /// 只比较文本会把同一个目录当成两个位置。尚不存在的末尾部分原样保留；链接最多跟随 32 次，防止循环链接。
    /// Windows 上只做 <see cref="Normalize"/>，不解析 junction 或符号链接。
    /// </remarks>
    /// <param name="path">相对或绝对路径。</param>
    /// <returns>规范化且（Unix 上）不含已存在符号链接的绝对路径。</returns>
    public static string ResolveLinks(string path) => ResolveLinks(path, 0);

    /// <summary>
    /// <see cref="ResolveLinks(string)"/> 的实现：逐级检查路径的每一部分，遇到符号链接时把链接目标整体再解析一遍
    /// （目标本身的上级目录也可能是链接，例如指向 <c>/var/...</c> 的链接）。
    /// </summary>
    /// <param name="path">相对或绝对路径。</param>
    /// <param name="depth">已经跟随的链接层数；超过 32 层时停止解析，防止循环链接。</param>
    /// <returns>解析后的绝对路径。</returns>
    private static string ResolveLinks(string path, int depth)
    {
        var full = Normalize(path);
        if (OperatingSystem.IsWindows() || depth > 32) return full;
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is { } target)
                current = ResolveLinks(Path.GetFullPath(target, Path.GetDirectoryName(current)!), depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>
    /// 判断两个路径在解析符号链接（见 <see cref="ResolveLinks(string)"/>）之后是否指向同一位置。
    /// </summary>
    /// <param name="left">第一个路径。</param>
    /// <param name="right">第二个路径。</param>
    /// <returns>两者解析后按平台规则相等时返回 <see langword="true"/>。</returns>
    public static bool AreSameLocation(string left, string right) => AreSame(ResolveLinks(left), ResolveLinks(right));

    /// <summary>
    /// 判断 <paramref name="path"/> 是否就是 <paramref name="root"/> 或位于其下（仅比较文本，不解析链接）。
    /// </summary>
    /// <remarks>
    /// 两个参数都会先规范化，因此 <c>..</c>、末尾分隔符和大小写差异（Windows）不会绕过判断；
    /// <c>D:\app-data</c> 不会被误认为位于 <c>D:\app</c> 之下。
    /// </remarks>
    /// <param name="root">根目录。</param>
    /// <param name="path">要检查的路径。</param>
    /// <returns>位于根目录内（含根目录本身）时为 <see langword="true"/>。</returns>
    public static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Normalize(root);
        var normalizedPath = Normalize(path);
        if (AreSame(normalizedRoot, normalizedPath)) return true;
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, Comparison);
    }
}
