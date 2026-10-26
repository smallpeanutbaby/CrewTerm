using System.Runtime.Versioning;
using CrewTerm.Workspace.Git;
using CrewTerm.Workspace.Tests.Fixtures;

namespace CrewTerm.Workspace.Tests.Git;

/// <summary>
/// 覆盖 <see cref="GitService"/> 读取改动、查看 diff、暂存文件与管理 worktree 的行为。
/// 每个测试通过 <see cref="TempWorkspace.InitRepo"/> 在临时目录中创建真实的 Git 仓库，并调用本机真实的 <c>git</c> 命令。
/// </summary>
public class GitServiceTests
{
    /// <summary>
    /// 验证：对文件名含方括号、空格和中文的 <c>a [1] 中文.cs</c>，能读到工作区修改状态 <c> M</c> 及其 diff（不混入另一个文件的改动）；
    /// 暂存时按字面量匹配文件名，只暂存该文件（状态变为 <c>M </c>），而 <c>[1]</c> 若被当作通配符就会匹配到的 <c>a 1 中文.cs</c> 仍未暂存，
    /// 且暂存区 diff 中包含该修改。
    /// </summary>
    [Fact]
    public async Task ReadsRealChangesDiffAndStagesLiteralFilename()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        repo.Write("a [1] 中文.cs", "before\n");
        repo.Write("a 1 中文.cs", "unrelated\n");
        await repo.Git("add", ".");
        await repo.Git("commit", "-m", "files");
        repo.Write("a [1] 中文.cs", "after\n");
        repo.Write("a 1 中文.cs", "do not stage\n");
        var git = new GitService();
        var changes = await git.GetChangesAsync(repo.Root);
        Assert.Contains(changes, change => change.RelativePath == "a [1] 中文.cs" && change.Status == " M");
        var diff = await git.GetDiffAsync(repo.Root, "a [1] 中文.cs");
        Assert.Contains("-before", diff);
        Assert.Contains("+after", diff);
        Assert.DoesNotContain("do not stage", diff);
        await git.StageAsync(repo.Root, "a [1] 中文.cs");
        changes = await git.GetChangesAsync(repo.Root);
        Assert.Contains(changes, change => change.RelativePath == "a [1] 中文.cs" && change.Status == "M ");
        Assert.Contains(changes, change => change.RelativePath == "a 1 中文.cs" && change.Status == " M");
        Assert.Contains("+after", await git.GetDiffAsync(repo.Root, "a [1] 中文.cs", true));
    }

    /// <summary>
    /// 验证：位于含空格子目录中的未跟踪文件会以完整相对路径 <c>new folder/new.txt</c> 列出（而不是只列出目录），
    /// 标记为未跟踪，其 diff 显示为整文件新增。
    /// </summary>
    [Fact]
    public async Task ReadsUntrackedFileDiffAndNestedPaths()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        repo.Write("new folder/new.txt", "new content\n");
        var git = new GitService();
        var change = Assert.Single(await git.GetChangesAsync(repo.Root));
        Assert.Equal("new folder/new.txt", change.RelativePath);
        Assert.True(change.IsUntracked);
        Assert.Contains("+new content", await git.GetDiffAsync(repo.Root, change.RelativePath));
    }

    /// <summary>
    /// 验证：<c>git mv</c> 产生的重命名作为一条已暂存的改动返回，同时保留新文件名 <c>new name.txt</c> 和原文件名 <c>initial.txt</c>。
    /// </summary>
    [Fact]
    public async Task PreservesRenameOldAndNewNames()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        await repo.Git("mv", "initial.txt", "new name.txt");
        var change = Assert.Single(await new GitService().GetChangesAsync(repo.Root));
        Assert.Equal("new name.txt", change.RelativePath);
        Assert.Equal("initial.txt", change.OriginalPath);
        Assert.True(change.IsStaged);
    }

    /// <summary>
    /// 验证：已在磁盘上删除的已跟踪文件可以被暂存（状态变为 <c>D </c>）；暂存 <c>../outside.txt</c> 这类项目外路径时抛出
    /// <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    [Fact]
    public async Task StagesDeletedFilesAndRejectsOutsidePaths()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        File.Delete(System.IO.Path.Combine(repo.Root, "initial.txt"));
        var git = new GitService();
        await git.StageAsync(repo.Root, "initial.txt");
        Assert.Equal("D ", Assert.Single(await git.GetChangesAsync(repo.Root)).Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => git.StageAsync(repo.Root, "../outside.txt"));
    }

    /// <summary>
    /// 验证：在含空格的路径上以新分支 <c>feature/test</c> 创建 worktree 后，返回的路径与分支正确、worktree 列表变为 2 项且已检出文件；
    /// 删除该 worktree 后目录消失、列表只剩主工作区，而分支 <c>feature/test</c> 仍然保留。
    /// </summary>
    [Fact]
    public async Task CreatesListsAndRemovesWorktreeWithoutDeletingBranch()
    {
        using var repo = new TempWorkspace();
        using var target = new TempWorkspace();
        await repo.InitRepo();
        var git = new GitService();
        var path = System.IO.Path.Combine(target.Root, "feature work");
        var created = await git.CreateWorktreeAsync(repo.Root, path, "feature/test");
        Assert.Equal(path, created.Path);
        Assert.Equal("feature/test", created.Branch);
        Assert.Equal(2, (await git.ListWorktreesAsync(repo.Root)).Count);
        Assert.True(File.Exists(System.IO.Path.Combine(path, "initial.txt")));
        await git.RemoveWorktreeAsync(repo.Root, path);
        Assert.False(Directory.Exists(path));
        Assert.Single(await git.ListWorktreesAsync(repo.Root));
        Assert.Contains("feature/test", await repo.Git("branch", "--list", "feature/test"));
    }

    /// <summary>
    /// 验证：worktree 中存在未提交的修改时，删除抛出 <see cref="GitOperationException"/>，修改过的文件内容原样保留。
    /// </summary>
    [Fact]
    public async Task RefusesDirtyWorktreeRemoval()
    {
        using var repo = new TempWorkspace();
        using var target = new TempWorkspace();
        await repo.InitRepo();
        var git = new GitService();
        var path = System.IO.Path.Combine(target.Root, "feature");
        await git.CreateWorktreeAsync(repo.Root, path, "feature");
        File.WriteAllText(System.IO.Path.Combine(path, "initial.txt"), "my edits");
        await Assert.ThrowsAsync<GitOperationException>(() => git.RemoveWorktreeAsync(repo.Root, path));
        Assert.Equal("my edits", File.ReadAllText(System.IO.Path.Combine(path, "initial.txt")));
    }

    /// <summary>
    /// 验证：对不是 Git 仓库的目录读取改动时抛出 <see cref="GitOperationException"/>，消息中提及 git；
    /// 传入已取消的令牌时以 <see cref="OperationCanceledException"/>（或其派生类型）结束。
    /// </summary>
    [Fact]
    public async Task ReportsNonRepositoryAndHonorsCancellation()
    {
        using var root = new TempWorkspace();
        var git = new GitService();
        var error = await Assert.ThrowsAsync<GitOperationException>(() => git.GetChangesAsync(root.Root));
        Assert.Contains("git", error.Message, StringComparison.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => git.GetChangesAsync(root.Root, cancellation.Token));
    }

    /// <summary>
    /// 验证：项目是仓库的子目录 <c>src</c> 时，只返回该子目录范围内的改动（仓库根目录下 <c>initial.txt</c> 的修改被排除），
    /// 路径相对于项目目录（<c>code.cs</c>），并能用该相对路径取得 diff。
    /// </summary>
    [Fact]
    public async Task ProjectsInsideRepositoriesReceiveProjectRelativePaths()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        repo.Write("src/code.cs", "old\n");
        await repo.Git("add", ".");
        await repo.Git("commit", "-m", "source");
        repo.Write("src/code.cs", "new\n");
        repo.Write("initial.txt", "outside project\n");
        var git = new GitService();
        var project = System.IO.Path.Combine(repo.Root, "src");
        var change = Assert.Single(await git.GetChangesAsync(project));
        Assert.Equal("code.cs", change.RelativePath);
        Assert.Contains("+new", await git.GetDiffAsync(project, change.RelativePath));
    }

    /// <summary>
    /// 验证：worktree 路径带末尾目录分隔符时也能创建，返回的路径去掉了末尾分隔符；用同一个带分隔符的路径删除后，列表只剩主工作区。
    /// </summary>
    [Fact]
    public async Task WorktreePathWithTrailingSeparatorIsCreatedAndRemoved()
    {
        using var repo = new TempWorkspace();
        using var target = new TempWorkspace();
        await repo.InitRepo();
        var git = new GitService();
        var path = System.IO.Path.Combine(target.Root, "trailing") + System.IO.Path.DirectorySeparatorChar;
        var created = await git.CreateWorktreeAsync(repo.Root, path, "feature/trailing");
        Assert.Equal(System.IO.Path.TrimEndingDirectorySeparator(path), created.Path);
        await git.RemoveWorktreeAsync(repo.Root, path);
        Assert.Single(await git.ListWorktreesAsync(repo.Root));
    }

    /// <summary>
    /// 验证：搜索路径中没有 Git 时，操作以 <see cref="GitOperationException"/> 失败并提示安装 Git，
    /// 而不是把裸程序名交给系统——系统的默认搜索会先查找本进程的当前目录（不受信任的仓库可以在那里放一个同名程序），
    /// 也会找到本机 PATH 中真实的 git。
    /// </summary>
    [Fact]
    public async Task MissingGitOnSearchPathFailsInsteadOfFallingBackToSystemSearch()
    {
        using var repo = new TempWorkspace();
        await repo.InitRepo();
        var error = await Assert.ThrowsAsync<GitOperationException>(() => new GitService(repo.Root).GetChangesAsync(repo.Root));
        Assert.Contains("Cannot find Git", error.Message);
    }

    /// <summary>
    /// 验证（Linux / macOS）：Git 以搜索路径中解析出的绝对路径启动——搜索目录中放一个总是输出固定路径的伪造 <c>git</c>，
    /// 读取仓库根目录得到的就是它的输出。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task RunsTheGitResolvedFromTheSearchPath()
    {
        using var repo = new TempWorkspace();
        var git = repo.Write("fake bin/git", "#!/bin/sh\necho /resolved/from/search/path\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var root = await new GitService(System.IO.Path.GetDirectoryName(git)).GetRepositoryRootAsync(repo.Root);
        Assert.Equal("/resolved/from/search/path", root);
    }
}
