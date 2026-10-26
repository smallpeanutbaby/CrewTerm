using System.Text;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Files;
using CrewTerm.Workspace.Tests.Fixtures;

namespace CrewTerm.Workspace.Tests.Files;

/// <summary>
/// 覆盖 <see cref="FileWorkspace"/> 的目录浏览、路径边界与链接防护、文件打开限制、全文搜索，
/// 以及 <see cref="DocumentBuffer"/> 的撤销/重做、脏状态、编码与换行保留和外部修改冲突检测。
/// 每个测试在 <see cref="TempWorkspace"/> 提供的临时目录中操作真实文件（含真实符号链接）。
/// </summary>
public class FileWorkspaceTests
{
    /// <summary>
    /// 验证：列出根目录时目录排在文件之前（<c>src</c> 位于 <c>z.cs</c> 之前且标记为目录）；
    /// 列出子目录时返回以 <c>/</c> 分隔、相对项目根目录的路径 <c>src/a.cs</c>。
    /// </summary>
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

    /// <summary>
    /// 验证：用 <c>../</c> 跳出项目根目录的路径，无论是打开文件还是列出目录，都抛出 <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    [Fact]
    public void RejectsPathsOutsideProject()
    {
        using var temp = new TempWorkspace();
        Assert.Throws<UnauthorizedAccessException>(() => FileWorkspace.Open(temp.Root, "../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => FileWorkspace.ListDirectory(temp.Root, "../"));
    }

    /// <summary>
    /// 验证：项目内指向项目外目录的符号链接不能被用来打开外部文件（抛出 <see cref="UnauthorizedAccessException"/>），
    /// 该链接也不会出现在目录列表中。
    /// </summary>
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

    /// <summary>
    /// 验证：打开含 NUL 字节的二进制文件时抛出 <see cref="InvalidDataException"/>，不会把它当作文本载入编辑器。
    /// </summary>
    [Fact]
    public void RejectsBinaryFiles()
    {
        using var temp = new TempWorkspace();
        File.WriteAllBytes(System.IO.Path.Combine(temp.Root, "file.bin"), [1, 0, 2, 3]);
        Assert.Throws<InvalidDataException>(() => FileWorkspace.Open(temp.Root, "file.bin"));
    }

    /// <summary>
    /// 验证：打开比 4 MiB 上限多 1 字节的文件时抛出 <see cref="InvalidDataException"/>（在解码内容之前按文件大小拒绝）。
    /// </summary>
    [Fact]
    public void RejectsTooLargeFilesBeforeDecoding()
    {
        using var temp = new TempWorkspace();
        using (var file = File.Create(System.IO.Path.Combine(temp.Root, "large.txt"))) file.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => FileWorkspace.Open(temp.Root, "large.txt"));
    }

    /// <summary>
    /// 验证：两次修改后文档为脏；撤销一次回到 <c>first</c>，再撤销一次回到已保存的原文因而不再为脏；
    /// 重做后保存，磁盘内容变为 <c>first</c>，文档重新变为非脏状态。
    /// </summary>
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

    /// <summary>
    /// 验证：原文件为带 BOM 的 UTF-8 且使用 CRLF 换行时，以 LF 换行编辑的文本保存后仍以 BOM 开头、换行还原为 CRLF，
    /// 并且目录中只有这一个文件（没有遗留临时文件）。
    /// </summary>
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

    /// <summary>
    /// 验证：原文件为 UTF-16 LE（带 <c>FF FE</c> BOM）且使用 CRLF 换行时，保存后仍是 UTF-16 LE 编码，换行同样保持 CRLF。
    /// </summary>
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

    /// <summary>
    /// 验证：文档打开后文件被外部（如 Agent）改写，保存时抛出 <see cref="FileConflictException"/>，
    /// 磁盘上保留外部写入的内容，编辑器中的修改仍为未保存（脏）状态。
    /// </summary>
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

    /// <summary>
    /// 验证：文档打开后文件在编辑器之外被删除，保存时抛出 <see cref="FileConflictException"/>，不会悄悄重新创建该文件。
    /// </summary>
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

    /// <summary>
    /// 验证：搜索 <c>[thing]</c> 按字面量（方括号不当作正则字符类）且不区分大小写匹配，只返回 <c>src/a.cs</c> 第 2 行的
    /// <c>Find [Thing]</c>；<c>obj</c>、<c>.git</c> 目录下的同名内容以及二进制文件都被跳过。
    /// </summary>
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

    /// <summary>
    /// 验证：传入已取消的令牌时，<see cref="FileWorkspace.SearchAsync"/> 以 <see cref="OperationCanceledException"/>（或其派生类型）结束。
    /// </summary>
    [Fact]
    public async Task SearchHonorsCancellation()
    {
        using var temp = new TempWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileWorkspace.SearchAsync(temp.Root, "x", cancellation.Token));
    }

    /// <summary>
    /// 验证：链接工作树中以文件形式存在的 <c>.git</c>（内容为 <c>gitdir: ...</c>）不参与搜索，搜索 <c>gitdir</c> 没有结果。
    /// </summary>
    [Fact]
    public async Task SearchExcludesLinkedWorktreeGitMetadataFile()
    {
        using var root = new TempWorkspace();
        root.Write(".git", "gitdir: C:/other-repo/.git/worktrees/feature");
        Assert.Empty(await FileWorkspace.SearchAsync(root.Root, "gitdir"));
    }

    /// <summary>
    /// 验证：大写的 <c>.GIT</c> 目录同样被视为 Git 元数据并从目录列表中隐藏，列表中只剩 <c>visible.txt</c>。
    /// </summary>
    [Fact]
    public void ListingHidesGitMetadataRegardlessOfLetterCase()
    {
        using var root = new TempWorkspace();
        Directory.CreateDirectory(System.IO.Path.Combine(root.Root, ".GIT"));
        root.Write("visible.txt", "visible");
        var entry = Assert.Single(FileWorkspace.ListDirectory(root.Root));
        Assert.Equal("visible.txt", entry.Name);
    }
}
