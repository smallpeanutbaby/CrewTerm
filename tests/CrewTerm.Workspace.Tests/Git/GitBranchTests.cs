using System.Diagnostics;
using System.Runtime.Versioning;
using CrewTerm.Workspace.Git;
using CrewTerm.Workspace.Tests.Fixtures;

namespace CrewTerm.Workspace.Tests.Git;

/// <summary>
/// 覆盖 <see cref="GitBranch"/>：从 <c>.git/HEAD</c> 读取当前分支（含子目录向上查找、worktree 的 <c>gitdir:</c> 文件、分离 HEAD），
/// 以及对仓库中不可信内容的处理（控制字符、reftable、格式错误的 <c>.git</c> 文件、网络与设备路径、命名管道）。
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

    /// <summary>
    /// 验证：reftable 格式仓库的 <c>HEAD</c>（<c>ref: refs/heads/.invalid</c>）以及任何一段以 <c>.</c> 开头的引用都不显示为分支。
    /// </summary>
    [Fact]
    public void ReftablePlaceholderIsNotShownAsABranch()
    {
        Assert.Null(GitBranch.Parse("ref: refs/heads/.invalid\n"));
        Assert.Null(GitBranch.Parse("ref: refs/heads/feature/.hidden\n"));
        Assert.Equal("feature/x.y", GitBranch.Parse("ref: refs/heads/feature/x.y\n"));
    }

    /// <summary>
    /// 验证：内层目录的 <c>.git</c> 文件格式错误时返回 <see langword="null"/>，而不是继续向上报告外层仓库的分支；
    /// 空的 <c>HEAD</c> 文件同样为 <see langword="null"/>。
    /// </summary>
    [Fact]
    public void MalformedGitFileStopsTheSearch()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(".git/HEAD", "ref: refs/heads/main\n");
        workspace.Write("inner/.git", "not a gitdir line\n");
        Assert.Equal("main", GitBranch.Read(workspace.Root));
        Assert.Null(GitBranch.Read(Path.Combine(workspace.Root, "inner")));
        workspace.Write("empty/.git/HEAD", "");
        Assert.Null(GitBranch.Read(Path.Combine(workspace.Root, "empty")));
    }

    /// <summary>
    /// 验证：<c>gitdir:</c> 指向网络共享或设备路径时不跟随（返回 <see langword="null"/>），本地的相对与绝对路径照常跟随。
    /// </summary>
    [Fact]
    public void RemoteAndDeviceGitDirectoriesAreNotFollowed()
    {
        Assert.True(GitBranch.IsRemoteOrDevicePath(@"\\attacker\share\x"));
        Assert.True(GitBranch.IsRemoteOrDevicePath("//attacker/share/x"));
        Assert.True(GitBranch.IsRemoteOrDevicePath(@"\\?\C:\x"));
        Assert.True(GitBranch.IsRemoteOrDevicePath(@"\\.\pipe\x"));
        Assert.True(GitBranch.IsRemoteOrDevicePath(@"\??\C:\x"));
        Assert.False(GitBranch.IsRemoteOrDevicePath("../main/.git/worktrees/x"));
        Assert.False(GitBranch.IsRemoteOrDevicePath(@"C:\repo\.git"));
        Assert.False(GitBranch.IsRemoteOrDevicePath("/home/user/repo/.git"));

        using var workspace = new TempWorkspace();
        workspace.Write("remote/.git", "gitdir: \\\\attacker.invalid\\share\\x\n");
        workspace.Write("slashes/.git", "gitdir: //attacker.invalid/share/x\n");
        workspace.Write("main/.git/worktrees/local/HEAD", "ref: refs/heads/local\n");
        workspace.Write("local/.git", "gitdir: " + Path.Combine(workspace.Root, "main", ".git", "worktrees", "local") + "\n");
        Assert.Null(GitBranch.Read(Path.Combine(workspace.Root, "remote")));
        Assert.Null(GitBranch.Read(Path.Combine(workspace.Root, "slashes")));
        Assert.Equal("local", GitBranch.Read(Path.Combine(workspace.Root, "local")));
    }

    /// <summary>
    /// 验证（Linux / macOS）：<c>HEAD</c> 是命名管道（FIFO）时立即返回 <see langword="null"/>，不会因打开管道等待写入者而永远阻塞。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void NamedPipeHeadDoesNotBlock()
    {
        using var workspace = new TempWorkspace();
        var gitDirectory = Directory.CreateDirectory(Path.Combine(workspace.Root, ".git")).FullName;
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [Path.Combine(gitDirectory, "HEAD")]))!)
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }
        var read = Task.Run(() => GitBranch.Read(workspace.Root));
        Assert.True(read.Wait(TimeSpan.FromSeconds(10)), "Reading a FIFO HEAD blocked.");
        Assert.Null(read.Result);
    }
}
