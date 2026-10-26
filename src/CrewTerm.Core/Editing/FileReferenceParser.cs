using System.Text.RegularExpressions;

namespace CrewTerm.Core.Editing;

/// <summary>
/// 解析编译器、测试框架和终端输出中常见的文件位置写法。
/// </summary>
/// <remarks>
/// 支持 <c>path:line</c>、<c>path:line:column</c>（GCC、TypeScript、Rust 等）以及
/// <c>path(line)</c>、<c>path(line,column)</c>（MSBuild/C#）。路径必须带扩展名；
/// 正则使用 <see cref="RegexOptions.NonBacktracking"/>，避免恶意输入导致回溯爆炸。
/// </remarks>
public static partial class FileReferenceParser
{
    /// <summary>
    /// 参与解析的最大文本长度；更长的文本直接视为无匹配。
    /// </summary>
    public const int MaximumTextLength = 16384;

    /// <summary>
    /// 编译期生成的文件位置正则：可选盘符 + 带扩展名的路径，后接 <c>:行[:列]</c> 或 <c>(行[,列])</c>。
    /// </summary>
    /// <returns>可复用的正则实例。</returns>
    [GeneratedRegex("(?<path>(?:[A-Za-z]:[\\\\/])?[^\\r\\n<>:\"|?*]+?\\.[A-Za-z0-9]+)(?::(?<line>\\d+)(?::(?<col>\\d+))?|\\((?<line>\\d+)(?:,\\s*(?<col>\\d+))?\\))", RegexOptions.NonBacktracking)]
    private static partial Regex LocationPattern();

    /// <summary>
    /// 解析文本中的第一个文件位置。
    /// </summary>
    /// <param name="text">一行输出或用户输入的路径。</param>
    /// <param name="root">相对路径的基准目录（通常是项目根目录）。</param>
    /// <returns>解析结果；没有可识别的位置或路径非法时返回 <see langword="null"/>。</returns>
    public static FileLocation? Parse(string text, string root)
    {
        if (text.Length > MaximumTextLength) return null;
        var match = LocationPattern().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line) || line < 1) return null;
        var column = int.TryParse(match.Groups["col"].Value, out var c) && c > 0 ? c : 1;
        try
        {
            var path = match.Groups["path"].Value.Trim().Trim('\'', '"');
            return new FileLocation(System.IO.Path.GetFullPath(path, root), line, column);
        }
        catch (ArgumentException) { return null; }
    }
}
