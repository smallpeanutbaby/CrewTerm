namespace CrewTerm.Workspace.Files;

/// <summary>
/// 项目全文搜索的一条结果。
/// </summary>
/// <param name="RelativePath">以 <c>/</c> 分隔的项目相对路径。</param>
/// <param name="LineNumber">行号，从 1 开始。</param>
/// <param name="Text">命中行的完整文本。</param>
public sealed record SearchResult(string RelativePath, int LineNumber, string Text);
