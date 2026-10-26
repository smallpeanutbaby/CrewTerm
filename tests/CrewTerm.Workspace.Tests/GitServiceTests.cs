namespace CrewTerm.Workspace.Tests;

public class GitServiceTests
{
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
}
