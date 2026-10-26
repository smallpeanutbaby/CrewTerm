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
