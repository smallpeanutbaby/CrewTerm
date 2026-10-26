using CrewTerm.Core.Editing;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Files;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 把 Terminal.Gui 代码编辑器控件与 <see cref="DocumentBuffer"/> 绑定：打开、保存、关闭、重新加载与生成代码上下文。
/// </summary>
/// <remarks>
/// 编辑器控件负责交互式编辑与撤销栈；<see cref="DocumentBuffer"/> 负责编码、换行与外部修改冲突检测。
/// 所有“替换当前文件”的操作在有未保存修改时都需要调用方明确传入 <c>discardConfirmed</c>。
/// 本类不是线程安全的，所有成员都应在 UI 线程调用。
/// </remarks>
public sealed class EditorBinding : IDisposable
{
    /// <summary>
    /// 当前打开文件的磁盘缓冲（记录编码、换行风格与打开时的磁盘状态）；未打开文件时为 <see langword="null"/>。
    /// </summary>
    private DocumentBuffer? buffer;

    /// <summary>
    /// 当前文件所属的项目根目录，用于重新加载与生成代码上下文；未打开文件时为 <see langword="null"/>。
    /// </summary>
    private string? projectRoot;

    /// <summary>
    /// 带行号、折叠与语法高亮的编辑器控件：填满父视图、显示滚动条、不自动换行，按 Tab 时插入空格。
    /// </summary>
    /// <remarks>
    /// 调用方负责把它加入界面；它随 <see cref="Dispose"/> 一起释放。
    /// </remarks>
    public Editor View { get; } = new()
    {
        Width = Dim.Fill(), Height = Dim.Fill(),
        GutterOptions = GutterOptions.LineNumbers | GutterOptions.Folding,
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        WordWrap = false, ConvertTabsToSpaces = true
    };

    /// <summary>
    /// 是否打开了文件且有未保存修改。
    /// </summary>
    /// <remarks>
    /// 以撤销栈上的“原始文件”标记为基准：打开、保存或关闭时都会重新标记，撤销回标记点后也视为未修改。
    /// 没有打开文件时始终为 <see langword="false"/>。
    /// </remarks>
    public bool IsDirty => buffer is not null && View.IsModified;

    /// <summary>
    /// 当前文件的绝对路径；未打开文件时为 <see langword="null"/>。
    /// </summary>
    public string? Path => buffer?.Path;

    /// <summary>
    /// 编辑器控件当前显示的文档。
    /// </summary>
    /// <exception cref="InvalidOperationException">编辑器控件当前没有文档。</exception>
    private TextDocument Document => View.Document ?? throw new InvalidOperationException("No editor document is open");

    /// <summary>
    /// 打开项目内的文件，并按扩展名选择语法高亮；成功后撤销栈以新文件内容为“未修改”基准。
    /// </summary>
    /// <remarks>
    /// 文件读取失败时异常会原样抛出，此时编辑器仍保留原来的文件与内容。
    /// </remarks>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">项目相对路径。</param>
    /// <param name="discardConfirmed">用户已确认放弃当前未保存的修改。</param>
    /// <returns>因有未保存修改且未确认放弃而没有打开时为 <see langword="false"/>；打开成功为 <see langword="true"/>。</returns>
    /// <exception cref="DirectoryNotFoundException">项目根目录不存在。</exception>
    /// <exception cref="UnauthorizedAccessException">路径为绝对路径、越出项目根目录或经过符号链接。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="InvalidDataException">文件超过 4 MiB、不是有效的 UTF-8/UTF-16/UTF-32 文本或包含 NUL（二进制）。</exception>
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

    /// <summary>
    /// 保存当前文件；磁盘内容已被外部修改时抛出 <see cref="FileConflictException"/>，编辑内容保持不变。
    /// 没有打开文件时什么也不做。
    /// </summary>
    /// <remarks>
    /// 保存沿用文件打开时的编码与换行风格，并以原子替换方式写盘；成功后撤销栈以当前内容为“未修改”基准。
    /// </remarks>
    /// <exception cref="FileConflictException">文件在打开后被外部修改或删除。</exception>
    /// <exception cref="InvalidDataException">保存后的内容超过 4 MiB 上限。</exception>
    public void Save()
    {
        if (buffer is null) return;
        buffer.SetText(View.Text);
        buffer.Save();
        Document.UndoStack.MarkAsOriginalFile();
    }

    /// <summary>
    /// 关闭当前文件并清空编辑器：控件换成一个新的空文档，撤销历史随旧文档一起丢弃。
    /// </summary>
    /// <param name="discardConfirmed">用户已确认放弃未保存的修改。</param>
    /// <returns>因未保存修改而未关闭时为 <see langword="false"/>；已关闭（包括原本就没有打开文件）时为 <see langword="true"/>。</returns>
    public bool Close(bool discardConfirmed = false)
    {
        if (IsDirty && !discardConfirmed) return false;
        buffer = null;
        projectRoot = null;
        View.Document = new TextDocument("");
        Document.UndoStack.MarkAsOriginalFile();
        return true;
    }

    /// <summary>
    /// 在文件被外部操作（如检查点恢复）改写后，若编辑器仍显示同一文档且没有新的修改，则重新加载。
    /// </summary>
    /// <remarks>
    /// 文档对象按引用比较：打开、关闭或重新加载都会换成新的 <see cref="TextDocument"/>，
    /// 因此只要期间发生过任何文件切换就不会误把旧路径重新加载进来。
    /// </remarks>
    /// <param name="expectedDocument">操作开始前编辑器中的文档对象；用户期间切换过文件则不重新加载。</param>
    /// <returns>
    /// 已重新加载（或文件已被删除而关闭）时为 <see langword="true"/>；
    /// 没有打开文件、有未保存修改或文档已被替换时为 <see langword="false"/>，编辑器保持不变。
    /// </returns>
    /// <exception cref="InvalidDataException">文件被改写成无法编辑的内容（过大、二进制或无效编码）。</exception>
    public bool TryReloadUnchanged(TextDocument? expectedDocument)
    {
        if (buffer is null || projectRoot is null || IsDirty || !ReferenceEquals(View.Document, expectedDocument))
            return false;
        if (!File.Exists(buffer.Path)) return Close();
        return Open(projectRoot, System.IO.Path.GetRelativePath(projectRoot, buffer.Path));
    }

    /// <summary>
    /// 把光标移动到指定行首；行号超出范围时取最近的有效行。
    /// </summary>
    /// <remarks>
    /// 只移动光标，不切换焦点也不显示编辑区；调用方需要自行处理。
    /// </remarks>
    /// <param name="line">行号，从 1 开始。</param>
    /// <exception cref="InvalidOperationException">编辑器控件当前没有文档。</exception>
    public void GoToLine(int line)
    {
        line = Math.Clamp(line, 1, Math.Max(1, Document.LineCount));
        View.CaretOffset = Document.GetLineByNumber(line).Offset;
    }

    /// <summary>
    /// 用编辑器中的当前文本（可能未保存）生成发送给 Agent 的代码上下文。
    /// </summary>
    /// <param name="instruction">放在上下文最前面的指令文本。</param>
    /// <param name="startLine">起始行号（从 1 开始，含）。</param>
    /// <param name="endLine">结束行号（含）。</param>
    /// <returns>以 <c>\n</c> 分隔的上下文文本：指令、文件相对路径与行号范围、所选代码。</returns>
    /// <exception cref="InvalidOperationException">尚未打开文件。</exception>
    /// <exception cref="ArgumentOutOfRangeException">行号超出文档范围。</exception>
    /// <exception cref="ArgumentException">所选内容超过 <see cref="CodeContext.MaximumCharacters"/>。</exception>
    public string CreateContext(string instruction, int startLine, int endLine)
    {
        if (buffer is null || projectRoot is null) throw new InvalidOperationException("Open a file first");
        return CodeContext.Build(projectRoot, buffer.Path, View.Text, startLine, endLine, instruction);
    }

    /// <summary>
    /// 释放编辑器控件。
    /// </summary>
    public void Dispose() => View.Dispose();
}
