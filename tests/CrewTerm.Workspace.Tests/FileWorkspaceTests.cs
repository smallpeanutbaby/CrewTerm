using System.Text;

namespace CrewTerm.Workspace.Tests;

public class FileWorkspaceTests
{
    [Fact]
    public void ListsDirectoriesFirstAndKeepsRelativePaths()
    {
        using var temp = new TempWorkspace();
        temp.Write("z.cs", "class Z {}");
        temp.Write("src/a.cs", "class A {}");
        var entries = FileWorkspace.ListDirectory(temp.Root);
        Assert.Equal("src", entries[0].Name);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("src/a.cs", Assert.Single(FileWorkspace.ListDirectory(temp.Root, "src")).RelativePath);
    }

    [Fact]
    public void RejectsPathsOutsideProject()
    {
        using var temp = new TempWorkspace();
        Assert.Throws<UnauthorizedAccessException>(() => FileWorkspace.Open(temp.Root, "../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => FileWorkspace.ListDirectory(temp.Root, "../"));
    }

    [Fact]
    public void RejectsSymlinkTraversal()
    {
        using var temp = new TempWorkspace();
        using var outside = new TempWorkspace();
        outside.Write("secret.txt", "outside");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(temp.Root, "link"), outside.Root);
        Assert.Throws<UnauthorizedAccessException>(() => FileWorkspace.Open(temp.Root, "link/secret.txt"));
        Assert.DoesNotContain(FileWorkspace.ListDirectory(temp.Root), x => x.Name == "link");
    }

    [Fact]
    public void RejectsBinaryFiles()
    {
        using var temp = new TempWorkspace();
        File.WriteAllBytes(System.IO.Path.Combine(temp.Root, "file.bin"), [1, 0, 2, 3]);
        Assert.Throws<InvalidDataException>(() => FileWorkspace.Open(temp.Root, "file.bin"));
    }

    [Fact]
    public void RejectsTooLargeFilesBeforeDecoding()
    {
        using var temp = new TempWorkspace();
        using (var file = File.Create(System.IO.Path.Combine(temp.Root, "large.txt"))) file.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => FileWorkspace.Open(temp.Root, "large.txt"));
    }

    [Fact]
    public void EditsUndoRedoAndDirtyStateTrackSavedText()
    {
        using var temp = new TempWorkspace();
        temp.Write("file.txt", "original");
        var doc = FileWorkspace.Open(temp.Root, "file.txt");
        doc.SetText("first");
        doc.SetText("second");
        Assert.True(doc.IsDirty);
        Assert.True(doc.Undo());
        Assert.Equal("first", doc.Text);
        Assert.True(doc.Undo());
        Assert.False(doc.IsDirty);
        Assert.True(doc.Redo());
        doc.Save();
        Assert.Equal("first", File.ReadAllText(doc.Path));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void SavePreservesUtf8BomAndCrlf()
    {
        using var temp = new TempWorkspace();
        var path = System.IO.Path.Combine(temp.Root, "file.cs");
        File.WriteAllText(path, "第一行\r\nsecond\r\n", new UTF8Encoding(true));
        var doc = FileWorkspace.Open(temp.Root, "file.cs");
        doc.SetText("第一行\nchanged\n");
        doc.Save();
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("第一行\r\nchanged\r\n", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(temp.Root));
    }

    [Fact]
    public void SavePreservesUtf16Encoding()
    {
        using var temp = new TempWorkspace();
        var path = System.IO.Path.Combine(temp.Root, "file.txt");
        File.WriteAllText(path, "你好\r\n", Encoding.Unicode);
        var doc = FileWorkspace.Open(temp.Root, "file.txt");
        doc.SetText("世界\n");
        doc.Save();
        Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(path)[..2]);
        Assert.Equal("世界\r\n", File.ReadAllText(path, Encoding.Unicode));
    }

    [Fact]
    public void RefusesToOverwriteChangesMadeByAgent()
    {
        using var temp = new TempWorkspace();
        temp.Write("file.txt", "before");
        var doc = FileWorkspace.Open(temp.Root, "file.txt");
        doc.SetText("mine");
        File.WriteAllText(doc.Path, "agent");
        Assert.Throws<FileConflictException>(() => doc.Save());
        Assert.Equal("agent", File.ReadAllText(doc.Path));
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void RefusesToRecreateFileDeletedOutsideEditor()
    {
        using var temp = new TempWorkspace();
        temp.Write("file.txt", "before");
        var doc = FileWorkspace.Open(temp.Root, "file.txt");
        doc.SetText("mine");
        File.Delete(doc.Path);
        Assert.Throws<FileConflictException>(() => doc.Save());
        Assert.False(File.Exists(doc.Path));
    }

    [Fact]
    public async Task SearchFindsLiteralMatchesAndSkipsGeneratedAndBinaryFiles()
    {
        using var temp = new TempWorkspace();
        temp.Write("src/a.cs", "first\nFind [Thing]\nlast");
        temp.Write("obj/temp.cs", "find [thing]");
        temp.Write(".git/temp", "find [thing]");
        File.WriteAllBytes(System.IO.Path.Combine(temp.Root, "binary.dat"), [0, 1, 2]);
        var result = Assert.Single(await FileWorkspace.SearchAsync(temp.Root, "[thing]"));
        Assert.Equal("src/a.cs", result.RelativePath);
        Assert.Equal(2, result.LineNumber);
        Assert.Equal("Find [Thing]", result.Text);
    }

    [Fact]
    public async Task SearchHonorsCancellation()
    {
        using var temp = new TempWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileWorkspace.SearchAsync(temp.Root, "x", cancellation.Token));
    }

    [Fact]
    public async Task SearchExcludesLinkedWorktreeGitMetadataFile()
    {
        using var root = new TempWorkspace();
        root.Write(".git", "gitdir: C:/other-repo/.git/worktrees/feature");
        Assert.Empty(await FileWorkspace.SearchAsync(root.Root, "gitdir"));
    }
}
