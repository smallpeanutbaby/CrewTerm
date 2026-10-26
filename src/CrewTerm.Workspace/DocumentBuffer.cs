using System.Security.Cryptography;
using System.Text;

namespace CrewTerm.Workspace;

public sealed class FileConflictException(string path)
    : IOException($"File changed outside the editor: {path}. Reload or compare before saving.")
{
    public string FilePath { get; } = path;
}

public sealed class DocumentBuffer
{
    private readonly string _root;
    private readonly string _relativePath;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly string _newline;
    private readonly List<string> _undo = [];
    private readonly Stack<string> _redo = [];
    private byte[] _originalBytes;
    private string _savedText;

    public string Path { get; }
    public string Text { get; private set; }
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);
    public string EncodingName => _encoding.WebName;
    public string Newline => _newline;

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

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = NormalizeNewlines(text);
        if (text == Text) return;
        _undo.Add(Text);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        Text = text;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Text);
        Text = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var text)) return false;
        _undo.Add(Text);
        Text = text;
        return true;
    }

    public void Save()
    {
        if (!IsDirty) return;
        var bytes = _encoding.GetBytes(Text.Replace("\n", _newline, StringComparison.Ordinal));
        var content = new byte[_preamble.Length + bytes.Length];
        _preamble.CopyTo(content, 0);
        bytes.CopyTo(content, _preamble.Length);
        if (content.Length > FileWorkspace.MaximumFileBytes) throw new InvalidDataException("The edited file exceeds the 4 MiB limit.");
        EnsureUnchanged();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $".crewterm-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            EnsureUnchanged();
            File.Move(temporary, Path, true);
            _originalBytes = content;
            _savedText = Text;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureUnchanged()
    {
        if (!File.Exists(Path)) throw new FileConflictException(Path);
        WorkspacePath.Resolve(_root, _relativePath);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(Path)), SHA256.HashData(_originalBytes)))
            throw new FileConflictException(Path);
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
