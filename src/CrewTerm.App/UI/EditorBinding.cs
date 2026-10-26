using CrewTerm.Core;
using CrewTerm.Workspace;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.UI;

public sealed class EditorBinding : IDisposable
{
    private DocumentBuffer? buffer;
    private string? projectRoot;
    public Editor View { get; } = new()
    {
        Width = Dim.Fill(), Height = Dim.Fill(),
        GutterOptions = GutterOptions.LineNumbers | GutterOptions.Folding,
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        WordWrap = false, ConvertTabsToSpaces = true
    };
    public bool IsDirty => buffer is not null && View.IsModified;
    public string? Path => buffer?.Path;
    private TextDocument Document => View.Document ?? throw new InvalidOperationException("No editor document is open");

    public bool Open(string root, string relativePath, bool discardConfirmed = false)
    {
        if (IsDirty && !discardConfirmed) return false;
        var next = FileWorkspace.Open(root, relativePath);
        buffer = next;
        projectRoot = root;
        View.Document = new TextDocument(next.Text);
        Document.UndoStack.MarkAsOriginalFile();
        View.HighlightingDefinition = HighlightingManager.Instance.GetDefinitionByExtension(System.IO.Path.GetExtension(next.Path));
        return true;
    }

    public void Save()
    {
        if (buffer is null) return;
        buffer.SetText(View.Text);
        buffer.Save();
        Document.UndoStack.MarkAsOriginalFile();
    }

    public bool Close(bool discardConfirmed = false)
    {
        if (IsDirty && !discardConfirmed) return false;
        buffer = null;
        projectRoot = null;
        View.Document = new TextDocument("");
        Document.UndoStack.MarkAsOriginalFile();
        return true;
    }

    public bool TryReloadUnchanged(TextDocument? expectedDocument)
    {
        if (buffer is null || projectRoot is null || IsDirty || !ReferenceEquals(View.Document, expectedDocument))
            return false;
        if (!File.Exists(buffer.Path)) return Close();
        return Open(projectRoot, System.IO.Path.GetRelativePath(projectRoot, buffer.Path));
    }

    public void GoToLine(int line)
    {
        line = Math.Clamp(line, 1, Math.Max(1, Document.LineCount));
        View.CaretOffset = Document.GetLineByNumber(line).Offset;
    }

    public string CreateContext(string instruction, int startLine, int endLine)
    {
        if (buffer is null || projectRoot is null) throw new InvalidOperationException("Open a file first");
        return CodeContext.Build(projectRoot, buffer.Path, View.Text, startLine, endLine, instruction);
    }
    public void Dispose() => View.Dispose();
}
