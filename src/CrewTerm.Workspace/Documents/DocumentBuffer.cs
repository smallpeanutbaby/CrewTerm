using System.Security.Cryptography;
using System.Text;
using CrewTerm.Core.IO;
using CrewTerm.Workspace.Files;

namespace CrewTerm.Workspace.Documents;

/// <summary>
/// 一个已打开的文本文件：内存中的编辑状态、撤销/重做栈，以及保持原格式的安全保存。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>内存中统一使用 <c>\n</c> 换行；保存时还原为文件原有的换行风格（CRLF/LF/CR）。</description></item>
/// <item><description>保持原编码与 BOM（UTF-8、UTF-16 LE/BE、UTF-32 LE/BE）。</description></item>
/// <item><description>保存前比对打开时的原始字节，磁盘内容已变化则抛出 <see cref="FileConflictException"/>，绝不静默覆盖。</description></item>
/// </list>
/// 本类不是线程安全的。
/// </remarks>
public sealed class DocumentBuffer
{
    /// <summary>
    /// 撤销栈最多保留的历史版本数；超过时丢弃最旧的一项。
    /// </summary>
    private const int MaximumUndoDepth = 100;

    /// <summary>
    /// 项目根目录（已规范化），保存前与 <see cref="_relativePath"/> 一起重新校验文件仍在项目内。
    /// </summary>
    private readonly string _root;

    /// <summary>
    /// 文件相对于 <see cref="_root"/> 的路径。
    /// </summary>
    private readonly string _relativePath;

    /// <summary>
    /// 加载时检测到的编码（严格模式：遇到非法字节抛出异常而不是替换），保存时沿用。
    /// </summary>
    private readonly Encoding _encoding;

    /// <summary>
    /// 文件原有的 BOM 字节，保存时原样写回；没有 BOM 时为空数组。
    /// </summary>
    private readonly byte[] _preamble;

    /// <summary>
    /// 文件原有的换行符（<c>\r\n</c>、<c>\r</c> 或 <c>\n</c>），保存时把内存中的 <c>\n</c> 还原成它。
    /// 文件中只要出现 CRLF 就视为 CRLF；混合换行的文件保存后会统一为这一种。
    /// </summary>
    private readonly string _newline;

    /// <summary>
    /// 撤销栈：每次 <see cref="SetText"/> 之前的全文，末尾为最近的一项。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="List{T}"/> 而不是 <see cref="Stack{T}"/> 实现，便于超过 <see cref="MaximumUndoDepth"/> 时丢弃最旧的一项。
    /// </remarks>
    private readonly List<string> _undo = [];

    /// <summary>
    /// 重做栈：被 <see cref="Undo"/> 撤销掉的全文；任何新的 <see cref="SetText"/> 修改都会清空它。
    /// </summary>
    private readonly Stack<string> _redo = [];

    /// <summary>
    /// 最近一次加载或保存时的磁盘字节（含 BOM），保存前与磁盘当前内容比对以检测外部修改。
    /// </summary>
    private byte[] _originalBytes;

    /// <summary>
    /// 最近一次加载或保存时的文本（换行已规范化），用于判断 <see cref="IsDirty"/>。
    /// </summary>
    private string _savedText;

    /// <summary>
    /// 文件绝对路径。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 当前文本（换行已规范化为 <c>\n</c>）。
    /// </summary>
    public string Text { get; private set; }

    /// <summary>
    /// 当前文本与最近一次保存（或加载）的内容不同。
    /// </summary>
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);

    /// <summary>
    /// 检测到的文件编码名称，例如 <c>utf-8</c>。
    /// </summary>
    public string EncodingName => _encoding.WebName;

    /// <summary>
    /// 文件原有的换行符，保存时使用。
    /// </summary>
    public string Newline => _newline;

    /// <summary>
    /// 使用已解码的内容创建文档；只能通过 <see cref="Load"/> 调用。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">项目相对路径，保存前用于重新校验。</param>
    /// <param name="path">文件绝对路径。</param>
    /// <param name="bytes">磁盘上的原始字节。</param>
    /// <param name="encoding">检测到的编码。</param>
    /// <param name="preamble">原始 BOM 字节（可能为空）。</param>
    /// <param name="text">解码后的文本（保留原换行）。</param>
    private DocumentBuffer(string root, string relativePath, string path, byte[] bytes, Encoding encoding, byte[] preamble, string text)
    {
        _root = root;
        _relativePath = relativePath;
        Path = path;
        _originalBytes = bytes;
        _encoding = encoding;
        _preamble = preamble;
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
        Text = _savedText = NormalizeNewlines(text);
    }

    /// <summary>
    /// 从项目内加载文本文件。
    /// </summary>
    /// <param name="root">已规范化的项目根目录。</param>
    /// <param name="relativePath">项目相对路径。</param>
    /// <returns>已加载的文档。</returns>
    /// <exception cref="InvalidDataException">文件超过大小上限、不是有效的 Unicode 文本或包含 NUL（二进制）。</exception>
    internal static DocumentBuffer Load(string root, string relativePath)
    {
        var path = WorkspacePath.Resolve(root, relativePath);
        if (new FileInfo(path).Length > FileWorkspace.MaximumFileBytes)
            throw new InvalidDataException("Code files larger than 4 MiB cannot be edited in CrewTerm.");
        var bytes = File.ReadAllBytes(path);
        var (encoding, preambleLength) = DetectEncoding(bytes);
        string text;
        try { text = encoding.GetString(bytes.AsSpan(preambleLength)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("The file is not valid UTF-8/UTF-16/UTF-32 text.", error); }
        if (text.Contains('\0')) throw new InvalidDataException("Binary files cannot be edited in the code workspace.");
        return new DocumentBuffer(root, relativePath, path, bytes, encoding, bytes[..preambleLength], text);
    }

    /// <summary>
    /// 替换全文；内容有变化时压入撤销栈并清空重做栈。
    /// </summary>
    /// <param name="text">新文本，任意换行风格。</param>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = NormalizeNewlines(text);
        if (text == Text) return;
        _undo.Add(Text);
        if (_undo.Count > MaximumUndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        Text = text;
    }

    /// <summary>
    /// 撤销上一次 <see cref="SetText"/>。
    /// </summary>
    /// <returns>没有可撤销的内容时返回 <see langword="false"/>。</returns>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Text);
        Text = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>
    /// 重做上一次撤销。
    /// </summary>
    /// <returns>没有可重做的内容时返回 <see langword="false"/>。</returns>
    public bool Redo()
    {
        if (!_redo.TryPop(out var text)) return false;
        _undo.Add(Text);
        Text = text;
        return true;
    }

    /// <summary>
    /// 以原编码、BOM 和换行风格原子保存；未修改时不写盘。
    /// </summary>
    /// <exception cref="FileConflictException">文件在打开后被外部修改或删除。</exception>
    /// <exception cref="InvalidDataException">保存后的内容超过大小上限。</exception>
    public void Save()
    {
        if (!IsDirty) return;
        var bytes = _encoding.GetBytes(Text.Replace("\n", _newline, StringComparison.Ordinal));
        var content = new byte[_preamble.Length + bytes.Length];
        _preamble.CopyTo(content, 0);
        bytes.CopyTo(content, _preamble.Length);
        if (content.Length > FileWorkspace.MaximumFileBytes) throw new InvalidDataException("The edited file exceeds the 4 MiB limit.");
        EnsureUnchanged();
        // 临时文件写完后、替换前再检查一次，缩小与 Agent 并发写入之间的竞争窗口。
        AtomicFile.WriteAllBytes(Path, content, beforeCommit: EnsureUnchanged);
        _originalBytes = content;
        _savedText = Text;
    }

    /// <summary>
    /// 确认磁盘文件仍存在、仍位于项目内，且字节与最近一次加载/保存时一致。
    /// </summary>
    /// <remarks>
    /// 比较的是两份内容的 SHA-256；“仍位于项目内”由 <see cref="WorkspacePath.Resolve"/> 重新解析相对路径来保证，
    /// 防止路径上的某一级在打开后被替换为指向项目外的符号链接或目录联接。
    /// </remarks>
    /// <exception cref="FileConflictException">文件已被删除或内容已变化。</exception>
    /// <exception cref="UnauthorizedAccessException">路径现在经过符号链接或目录联接。</exception>
    private void EnsureUnchanged()
    {
        if (!File.Exists(Path)) throw new FileConflictException(Path);
        WorkspacePath.Resolve(_root, _relativePath);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(Path)), SHA256.HashData(_originalBytes)))
            throw new FileConflictException(Path);
    }

    /// <summary>
    /// 把 CRLF 与单独的 CR 统一转换为 LF。
    /// </summary>
    /// <param name="text">任意换行风格的文本。</param>
    /// <returns>只含 <c>\n</c> 换行的文本。</returns>
    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// 按 BOM 判断编码；没有 BOM 时按严格 UTF-8 解码（非法字节会抛出异常而不是替换为 �）。
    /// UTF-32 LE 的 BOM 以 UTF-16 LE 的 BOM 开头，因此必须先检查 4 字节的 BOM。
    /// </summary>
    /// <param name="bytes">文件的完整原始字节。</param>
    /// <returns>
    /// 严格模式的编码（非法字节在解码时抛出 <see cref="DecoderFallbackException"/>）与 BOM 的字节数（没有 BOM 时为 0）。
    /// 返回的编码对象本身不输出 BOM，BOM 由调用方单独保存与写回。
    /// </returns>
    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return (new UTF32Encoding(false, false, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return (new UTF32Encoding(true, false, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }
}
