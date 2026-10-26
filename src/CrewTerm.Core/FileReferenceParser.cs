using System.Text.RegularExpressions;

namespace CrewTerm.Core;

public sealed record FileLocation(string Path, int Line, int Column);

public static partial class FileReferenceParser
{
    [GeneratedRegex("(?<path>(?:[A-Za-z]:[\\\\/])?[^\\r\\n<>:\"|?*]+?\\.[A-Za-z0-9]+)(?::(?<line>\\d+)(?::(?<col>\\d+))?|\\((?<line>\\d+)(?:,\\s*(?<col>\\d+))?\\))", RegexOptions.NonBacktracking)]
    private static partial Regex LocationPattern();

    public static FileLocation? Parse(string text, string root)
    {
        if (text.Length > 16384) return null;
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
