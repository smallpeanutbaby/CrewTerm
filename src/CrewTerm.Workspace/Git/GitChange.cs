namespace CrewTerm.Workspace.Git;

/// <summary>
/// Git 工作区中的一项改动（来自 <c>git status --porcelain=v1</c>）。
/// </summary>
/// <param name="RelativePath">相对于项目根目录、以 <c>/</c> 分隔的路径；重命名时为新路径。</param>
/// <param name="Status">两个字符的状态码：第 1 位为暂存区，第 2 位为工作区，例如 <c>" M"</c>、<c>"A "</c>、<c>"??"</c>。</param>
/// <param name="OriginalPath">重命名或复制前的路径；其他情况为 <see langword="null"/>。</param>
public sealed record GitChange(string RelativePath, string Status, string? OriginalPath = null)
{
    /// <summary>
    /// 未跟踪的新文件。
    /// </summary>
    public bool IsUntracked => Status == "??";

    /// <summary>
    /// 暂存区中有改动。
    /// </summary>
    public bool IsStaged => Status.Length > 0 && Status[0] != ' ' && Status[0] != '?';

    /// <inheritdoc/>
    public override string ToString() => $"{Status} {RelativePath}";
}
