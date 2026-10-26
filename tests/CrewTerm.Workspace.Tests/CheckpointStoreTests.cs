namespace CrewTerm.Workspace.Tests;

public class CheckpointStoreTests
{
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
