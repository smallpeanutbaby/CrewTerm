using CrewTerm.Workspace.Git;
using CrewTerm.Workspace.Tests.Fixtures;

namespace CrewTerm.Workspace.Tests.Git;

/// <summary>
/// 覆盖 <see cref="GitBranch"/>：从 <c>.git/HEAD</c> 读取当前分支（含子目录向上查找、worktree 的 <c>gitdir:</c> 文件、分离 HEAD），
/// 以及对仓库中不可信内容的清理。
/// </summary>
public sealed class GitBranchTests
{
    /// <summary>
    /// 验证：普通仓库返回 <c>refs/heads/</c> 之后的分支名（可含斜杠）；从仓库的子目录也能向上找到；不在仓库中时为 <see langword="null"/>。
    /// </summary>
    [Fact]
    public void BranchIsReadFromHeadEvenFromASubdirectory()
    {
        using var workspace = new TempWorkspace();
        Assert.Null(GitBranch.Read(workspace.Root));
        workspace.Write(".git/HEAD", "ref: refs/heads/feature/login\n");
        var nested = Directory.CreateDirectory(Path.Combine(workspace.Root, "src", "app")).FullName;
        Assert.Equal("feature/login", GitBranch.Read(workspace.Root));
        Assert.Equal("feature/login", GitBranch.Read(nested));
    }

    /// <summary>
    /// 验证：worktree 中 <c>.git</c> 是写有相对 <c>gitdir:</c> 的文件时，读取其指向目录中的 <c>HEAD</c>。
    /// </summary>
    [Fact]
    public void WorktreeGitFileIsFollowed()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("main/.git/worktrees/review/HEAD", "ref: refs/heads/review\n");
        workspace.Write("review/.git", "gitdir: ../main/.git/worktrees/review\n");
        Assert.Equal("review", GitBranch.Read(Path.Combine(workspace.Root, "review")));
    }

    /// <summary>
    /// 验证：分离 HEAD 显示提交哈希前 7 位；空内容或无法识别的内容为 <see langword="null"/>；分支名中的控制字符被去掉，超长名称被截断。
    /// </summary>
    [Fact]
    public void HeadContentIsParsedConservatively()
    {
        Assert.Equal("0123abc", GitBranch.Parse("0123abc4567def0123abc4567def0123abc4567\n"));
        Assert.Null(GitBranch.Parse(null));
        Assert.Null(GitBranch.Parse(""));
        Assert.Null(GitBranch.Parse("not a ref"));
        Assert.Equal("evil", GitBranch.Parse("ref: refs/heads/evil\u001b[2J"));
        Assert.Equal(64, GitBranch.Parse("ref: refs/heads/" + new string('b', 200))!.Length);
    }
}
