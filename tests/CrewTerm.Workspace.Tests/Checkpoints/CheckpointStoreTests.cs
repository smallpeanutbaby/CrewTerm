using CrewTerm.Workspace.Checkpoints;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Tests.Fixtures;

namespace CrewTerm.Workspace.Tests.Checkpoints;

/// <summary>
/// 覆盖 <see cref="CheckpointStore"/> 的捕获、列出、预览与恢复流程，以及冲突检测、路径安全和大小上限。
/// 每个测试用两个 <see cref="TempWorkspace"/> 分别充当项目目录和（位于项目之外的）状态目录，在真实文件系统上操作。
/// </summary>
public class CheckpointStoreTests
{
    /// <summary>
    /// 验证：捕获时包含用户已有的修改，但跳过 <c>obj</c> 构建产物与 <c>.git</c> 元数据（只计 1 个文件），且检查点出现在列表中；
    /// Agent 修改文件并新增文件后，预览列出这 2 项变化；按预览恢复后修改被还原、新增文件被删除，<c>obj</c> 与 <c>.git</c> 下的文件保持不变。
    /// </summary>
    [Fact]
    public async Task CapturesExistingEditsAndRestoresOnlyReviewedSnapshot()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        root.Write("src/code.cs", "my pre-existing edits\n");
        root.Write("obj/output.dll", "generated");
        root.Write(".git/config", "private git metadata");
        var store = new CheckpointStore(state.Root);
        var checkpoint = await store.CaptureAsync(root.Root, "before agent");
        Assert.Equal(1, checkpoint.FileCount);
        Assert.Equal(checkpoint.Id, Assert.Single(store.List(root.Root)).Id);
        root.Write("src/code.cs", "agent edits\n");
        root.Write("new.txt", "agent added\n");
        var preview = await store.PreviewAsync(root.Root, checkpoint.Id);
        Assert.Equal(2, preview.Changes.Count);
        Assert.Contains(preview.Changes, change => change.RelativePath == "src/code.cs" && change.AfterText.Contains("pre-existing"));
        Assert.Contains(preview.Changes, change => change.RelativePath == "new.txt" && change.Kind == "Added");
        await store.RestoreAsync(preview);
        Assert.Equal("my pre-existing edits\n", File.ReadAllText(System.IO.Path.Combine(root.Root, "src/code.cs")));
        Assert.False(File.Exists(System.IO.Path.Combine(root.Root, "new.txt")));
        Assert.Equal("generated", File.ReadAllText(System.IO.Path.Combine(root.Root, "obj/output.dll")));
        Assert.Equal("private git metadata", File.ReadAllText(System.IO.Path.Combine(root.Root, ".git/config")));
    }

    /// <summary>
    /// 验证：生成预览之后，即使发生变化的是预览中未列出的文件（<c>b.txt</c>），恢复也抛出 <see cref="FileConflictException"/>，
    /// 两个文件都保持恢复前的当前内容，不会被部分还原。
    /// </summary>
    [Fact]
    public async Task RefusesRestoreIfAnyFileChangesAfterPreview()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        root.Write("a.txt", "a before");
        root.Write("b.txt", "b before");
        var store = new CheckpointStore(state.Root);
        var checkpoint = await store.CaptureAsync(root.Root, "before");
        root.Write("a.txt", "a changed");
        var preview = await store.PreviewAsync(root.Root, checkpoint.Id);
        root.Write("b.txt", "concurrent change");
        await Assert.ThrowsAsync<FileConflictException>(() => store.RestoreAsync(preview));
        Assert.Equal("a changed", File.ReadAllText(System.IO.Path.Combine(root.Root, "a.txt")));
        Assert.Equal("concurrent change", File.ReadAllText(System.IO.Path.Combine(root.Root, "b.txt")));
    }

    /// <summary>
    /// 验证：生成预览之后项目中又出现未经审阅的新文件时，恢复抛出 <see cref="FileConflictException"/>，该新文件保留不被删除。
    /// </summary>
    [Fact]
    public async Task RefusesRestoreIfNewFileAppearsAfterPreview()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        root.Write("a.txt", "before");
        var store = new CheckpointStore(state.Root);
        var checkpoint = await store.CaptureAsync(root.Root, "before");
        var preview = await store.PreviewAsync(root.Root, checkpoint.Id);
        root.Write("new.txt", "unreviewed");
        await Assert.ThrowsAsync<FileConflictException>(() => store.RestoreAsync(preview));
        Assert.Equal("unreviewed", File.ReadAllText(System.IO.Path.Combine(root.Root, "new.txt")));
    }

    /// <summary>
    /// 验证：捕获后被删除的二进制文件在预览中标记为 <c>Missing</c>，恢复后按原始字节（含 0 与 255）重建。
    /// </summary>
    [Fact]
    public async Task RestoresMissingFileAndPreservesBinaryBytes()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        var path = System.IO.Path.Combine(root.Root, "small.bin");
        File.WriteAllBytes(path, [0, 1, 2, 255]);
        var store = new CheckpointStore(state.Root);
        var checkpoint = await store.CaptureAsync(root.Root, "binary");
        File.Delete(path);
        var preview = await store.PreviewAsync(root.Root, checkpoint.Id);
        Assert.Equal("Missing", Assert.Single(preview.Changes).Kind);
        await store.RestoreAsync(preview);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(path));
    }

    /// <summary>
    /// 验证：以 <c>../outside</c> 这类非法标识预览检查点时抛出 <see cref="ArgumentException"/>，无法借标识访问检查点文件夹之外的路径；
    /// 状态目录位于项目内部时，捕获抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    [Fact]
    public async Task RejectsCheckpointTraversalAndStorageInsideProject()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        var store = new CheckpointStore(state.Root);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PreviewAsync(root.Root, "../outside"));
        var internalStore = new CheckpointStore(System.IO.Path.Combine(root.Root, "state"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => internalStore.CaptureAsync(root.Root, "unsafe location"));
    }

    /// <summary>
    /// 验证：项目中存在超过 4 MiB 单文件上限的文件时，捕获抛出 <see cref="InvalidDataException"/> 而不是悄悄跳过该文件，
    /// 且不会留下任何检查点。
    /// </summary>
    [Fact]
    public async Task FailsCaptureInsteadOfSilentlyOmittingOversizedFile()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        using (var file = File.Create(System.IO.Path.Combine(root.Root, "large.bin"))) file.SetLength(4 * 1024 * 1024 + 1);
        var store = new CheckpointStore(state.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CaptureAsync(root.Root, "large"));
        Assert.Empty(store.List(root.Root));
    }

    /// <summary>
    /// 验证：链接工作树中以文件形式存在的 <c>.git</c>（内容为 <c>gitdir: ...</c>）不会被纳入检查点，只捕获 1 个源文件。
    /// </summary>
    [Fact]
    public async Task DoesNotSnapshotLinkedWorktreeGitMetadataFile()
    {
        using var root = new TempWorkspace();
        using var state = new TempWorkspace();
        root.Write(".git", "gitdir: C:/other-repo/.git/worktrees/feature");
        root.Write("source.cs", "class Program {}");
        var checkpoint = await new CheckpointStore(state.Root).CaptureAsync(root.Root, "worktree");
        Assert.Equal(1, checkpoint.FileCount);
    }
}
