namespace CrewTerm.Core;

public static class CodeContext
{
    public static string Build(string projectRoot, string filePath, string text, int startLine, int endLine, string instruction)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Selected lines are outside the document");
        var selected = string.Join('\n', lines[(startLine - 1)..endLine]);
        if (selected.Length > 60000) throw new ArgumentException("Selected context exceeds 60,000 characters");
        var relative = Path.GetRelativePath(projectRoot, filePath);
        return $"{instruction}\nFile: {relative}:{startLine}-{endLine}\n\n{selected}\n";
    }
}
