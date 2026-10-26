namespace CrewTerm.Core.Editing;

/// <summary>
/// 生成发送给 Agent 的代码上下文文本：指令 + 文件相对路径与行号 + 选中的代码。
/// </summary>
/// <remarks>
/// 生成的文本只会以 bracketed paste 方式插入目标终端的输入区，不会自动按 Enter，
/// 由用户在终端中确认后再提交。
/// </remarks>
public static class CodeContext
{
    /// <summary>
    /// 上下文正文的最大字符数，防止一次粘贴过大的内容。
    /// </summary>
    public const int MaximumCharacters = 60000;

    /// <summary>
    /// 按选中的行范围构建代码上下文：第一行是指令，第二行是 <c>File: 相对路径:起始行-结束行</c>，
    /// 空一行后是选中的代码（换行统一为 <c>\n</c>），末尾带一个换行。
    /// </summary>
    /// <param name="projectRoot">项目根目录，用于计算相对路径。</param>
    /// <param name="filePath">源文件绝对路径。</param>
    /// <param name="text">编辑器中的当前全文（可能尚未保存）。</param>
    /// <param name="startLine">起始行号（从 1 开始，含）。</param>
    /// <param name="endLine">结束行号（含）。</param>
    /// <param name="instruction">放在最前面的指令，例如“Review this code”。</param>
    /// <returns>以 <c>\n</c> 分隔的上下文文本。</returns>
    /// <exception cref="ArgumentOutOfRangeException">行号超出文档范围。</exception>
    /// <exception cref="ArgumentException">选中内容超过 <see cref="MaximumCharacters"/>。</exception>
    public static string Build(string projectRoot, string filePath, string text, int startLine, int endLine, string instruction)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Selected lines are outside the document");
        var selected = string.Join('\n', lines[(startLine - 1)..endLine]);
        if (selected.Length > MaximumCharacters) throw new ArgumentException("Selected context exceeds 60,000 characters");
        var relative = Path.GetRelativePath(projectRoot, filePath);
        return $"{instruction}\nFile: {relative}:{startLine}-{endLine}\n\n{selected}\n";
    }
}
