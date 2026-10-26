namespace CrewTerm.Workspace.Git;

/// <summary>
/// 一个 Git worktree（来自 <c>git worktree list --porcelain</c>）。
/// </summary>
/// <param name="Path">worktree 根目录绝对路径。</param>
/// <param name="Branch">检出的分支名（已去掉 <c>refs/heads/</c>）；分离 HEAD 时为空字符串。</param>
/// <param name="Head">当前提交的哈希。</param>
/// <param name="IsBare">是否为裸仓库。</param>
/// <param name="IsDetached">是否处于分离 HEAD 状态。</param>
public sealed record GitWorktree(string Path, string Branch, string Head, bool IsBare, bool IsDetached);
