using CrewTerm.App.UI;
using CrewTerm.Workspace;

namespace CrewTerm.App.Tests;

public sealed class EditorBindingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermEditor", Guid.NewGuid().ToString("N"));
    public EditorBindingTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.cs"), "one\r\ntwo\r\nthree");
        File.WriteAllText(Path.Combine(root, "two.cs"), "other");
    }

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

    public void Dispose() => Directory.Delete(root, true);
}
