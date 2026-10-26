using CrewTerm.App.UI.Controls;
using CrewTerm.Workspace.Documents;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="EditorBinding"/>（内置编辑器与磁盘文件的绑定）的单元测试：在临时项目目录中准备两个文件，
/// 验证未保存修改的保护、保存、外部修改冲突、跳转行与代码上下文生成，以及检查点恢复完成后 <see cref="EditorBinding.TryReloadUnchanged"/> 的重新加载规则。
/// </summary>
public sealed class EditorBindingTests : IDisposable
{
    /// <summary>
    /// 临时项目目录（系统临时目录下的 <c>CrewTermEditor\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermEditor", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建临时项目目录，并写入以 CRLF 分隔三行（<c>one</c>、<c>two</c>、<c>three</c>）的 <c>one.cs</c> 和内容为 <c>other</c> 的 <c>two.cs</c>。
    /// </summary>
    public EditorBindingTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.cs"), "one\r\ntwo\r\nthree");
        File.WriteAllText(Path.Combine(root, "two.cs"), "other");
    }

    /// <summary>
    /// 验证：有未保存修改时 <c>Close()</c> 返回 false 并保持绑定；确认放弃后关闭成功，路径清空、不再处于脏状态且编辑器文本为空。
    /// </summary>
    [Fact]
    public void ClosingDirtyDocumentRequiresExplicitDiscardAndClearsBinding()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        binding.View.Document!.Insert(0, "unsaved");
        Assert.False(binding.Close());
        Assert.NotNull(binding.Path);
        Assert.True(binding.Close(discardConfirmed: true));
        Assert.Null(binding.Path);
        Assert.False(binding.IsDirty);
        Assert.Empty(binding.View.Text);
    }

    /// <summary>
    /// 验证：编辑后处于脏状态，<c>Save</c> 把实际编辑后的文本写入磁盘（保留原文件的 CRLF 换行），并清除脏状态。
    /// </summary>
    [Fact]
    public void EditorSavesActualEditedContentAndClearsDirtyState()
    {
        using var binding = new EditorBinding();
        Assert.True(binding.Open(root, "one.cs"));
        binding.View.Document!.Insert(0, "prefix ");
        Assert.True(binding.IsDirty);
        binding.Save();
        Assert.Equal("prefix one\r\ntwo\r\nthree", File.ReadAllText(Path.Combine(root, "one.cs")));
        Assert.False(binding.IsDirty);
    }

    /// <summary>
    /// 验证：有未保存修改时打开另一个文件返回 false，编辑器仍显示未保存的文本；确认放弃后才切换为另一个文件的内容。
    /// </summary>
    [Fact]
    public void OpeningAnotherFileDoesNotDiscardUnsavedText()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        binding.View.Document!.Insert(0, "mine ");
        Assert.False(binding.Open(root, "two.cs"));
        Assert.StartsWith("mine ", binding.View.Text);
        Assert.True(binding.Open(root, "two.cs", discardConfirmed: true));
        Assert.Equal("other", binding.View.Text);
    }

    /// <summary>
    /// 验证：文件在编辑期间被外部（例如 Agent）改写后，<c>Save</c> 抛出 <see cref="FileConflictException"/>，编辑器仍处于脏状态，磁盘上外部写入的内容没有被覆盖。
    /// </summary>
    [Fact]
    public void ExternalAgentEditStillConflictsThroughEditorUi()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        binding.View.Document!.Insert(0, "mine ");
        File.WriteAllText(Path.Combine(root, "one.cs"), "agent");
        Assert.Throws<FileConflictException>(() => binding.Save());
        Assert.True(binding.IsDirty);
        Assert.Equal("agent", File.ReadAllText(Path.Combine(root, "one.cs")));
    }

    /// <summary>
    /// 验证：<c>GoToLine(2)</c> 把光标移到文档第 2 行；<c>CreateContext</c> 按文档行号截取第 2 行，结果包含 <c>two</c> 而不包含 <c>three</c>。
    /// </summary>
    [Fact]
    public void GotoAndSelectedContextUseDocumentCoordinates()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        binding.GoToLine(2);
        Assert.Equal(2, binding.View.Document!.GetLocation(binding.View.CaretOffset).Line);
        var context = binding.CreateContext("Review", 2, 2);
        Assert.Contains("two", context);
        Assert.DoesNotContain("three", context);
    }

    /// <summary>
    /// 验证：检查点恢复改写磁盘文件期间，用户在同一文档中做了修改时，<c>TryReloadUnchanged</c> 返回 false，
    /// 编辑器保留原文档对象、未保存的文本（编辑器内以 LF 换行）、脏状态和文件路径。
    /// </summary>
    [Fact]
    public void RestoreCompletionPreservesEditsMadeWhileRestoreWasPending()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        var originalDocument = binding.View.Document;
        File.WriteAllText(Path.Combine(root, "one.cs"), "checkpoint content");
        binding.View.Document!.Insert(0, "unsaved ");

        Assert.False(binding.TryReloadUnchanged(originalDocument));

        Assert.Same(originalDocument, binding.View.Document);
        Assert.Equal("unsaved one\ntwo\nthree", binding.View.Text);
        Assert.True(binding.IsDirty);
        Assert.Equal(Path.Combine(root, "one.cs"), binding.Path);
    }

    /// <summary>
    /// 验证：检查点恢复期间用户切换打开了另一个文件时，以旧文档调用 <c>TryReloadUnchanged</c> 返回 false，编辑器保持当前打开的 <c>two.cs</c>（同一文档对象、内容、非脏、路径）不变。
    /// </summary>
    [Fact]
    public void RestoreCompletionPreservesTheFileOpenedWhileRestoreWasPending()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        var originalDocument = binding.View.Document;
        binding.Open(root, "two.cs");
        var currentDocument = binding.View.Document;
        File.WriteAllText(Path.Combine(root, "one.cs"), "checkpoint content");

        Assert.False(binding.TryReloadUnchanged(originalDocument));

        Assert.Same(currentDocument, binding.View.Document);
        Assert.Equal("other", binding.View.Text);
        Assert.False(binding.IsDirty);
        Assert.Equal(Path.Combine(root, "two.cs"), binding.Path);
    }

    /// <summary>
    /// 验证：仍是同一文档且没有修改时，<c>TryReloadUnchanged</c> 返回 true 并载入磁盘上的新内容（非脏、路径不变）；
    /// 之后编辑并保存不会被误判为外部冲突，磁盘内容为新内容加上本次编辑。
    /// </summary>
    [Fact]
    public void RestoreCompletionReloadsTheOriginalUnmodifiedFile()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        var originalDocument = binding.View.Document;
        File.WriteAllText(Path.Combine(root, "one.cs"), "checkpoint content");

        Assert.True(binding.TryReloadUnchanged(originalDocument));

        Assert.Equal("checkpoint content", binding.View.Text);
        Assert.False(binding.IsDirty);
        Assert.Equal(Path.Combine(root, "one.cs"), binding.Path);
        binding.View.Document!.Insert(0, "saved ");
        binding.Save();
        Assert.Equal("saved checkpoint content", File.ReadAllText(Path.Combine(root, "one.cs")));
    }

    /// <summary>
    /// 验证：恢复操作删除了已打开且未修改的文件时，<c>TryReloadUnchanged</c> 返回 true 并关闭该文件（路径为空、文本为空、非脏）。
    /// </summary>
    [Fact]
    public void RestoreCompletionClosesTheUnmodifiedFileRemovedByRestore()
    {
        using var binding = new EditorBinding();
        binding.Open(root, "one.cs");
        var originalDocument = binding.View.Document;
        File.Delete(Path.Combine(root, "one.cs"));

        Assert.True(binding.TryReloadUnchanged(originalDocument));

        Assert.Null(binding.Path);
        Assert.Empty(binding.View.Text);
        Assert.False(binding.IsDirty);
    }

    /// <summary>
    /// 递归删除临时项目目录。
    /// </summary>
    public void Dispose() => Directory.Delete(root, true);
}
