using System.Globalization;
using System.Text;

namespace CrewTerm.Terminal;

/// <summary>Incremental character-cell terminal state, independent of UI and native handles.</summary>
public sealed partial class TerminalScreen
{
    public const int DefaultForeground = 0xD0D7DE;
    public const int DefaultBackground = 0x0D1117;
    private readonly object _gate = new();
    private readonly StringBuilder _sequence = new();
    private readonly Queue<string> _history = new();
    private readonly List<string> _responses = [];
    private TerminalCell[][] _cells;
    private int _columns, _rows, _column, _row, _scrollTop, _scrollBottom;
    private int _foreground = DefaultForeground, _background = DefaultBackground;
    private bool _bold, _reverse, _wrapPending, _autoWrap = true;
    private bool _cursorVisible = true, _alternate, _bracketedPaste, _applicationCursor, _sgrMouse;
    private int _mouseTracking;
    private char? _highSurrogate;
    private ParseState _parseState;
    private (int Column, int Row) _savedCursor;
    private BufferState? _primary;

    public event Action<string>? ResponseRequested;

    public TerminalScreen(int columns, int rows)
    {
        ValidateSize(columns, rows);
        _columns = columns;
        _rows = rows;
        _scrollBottom = rows - 1;
        _cells = CreateCells(columns, rows);
    }

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] responses;
        lock (_gate)
        {
            foreach (var value in text) Consume(value);
            responses = _responses.ToArray();
            _responses.Clear();
        }
        // A reply writes native input. Invoke it outside the screen lock to avoid lock inversion
        // with a concurrent native resize that updates this screen.
        foreach (var response in responses) ResponseRequested?.Invoke(response);
    }

    public TerminalSnapshot Snapshot()
    {
        lock (_gate)
            return new(_columns, _rows, _column, _row, _cursorVisible,
                _cells.Select(line => line.ToArray()).ToArray(), _history.ToArray(), _alternate,
                _bracketedPaste, _applicationCursor, _mouseTracking, _sgrMouse);
    }

    public string GetText()
    {
        lock (_gate) return string.Join('\n', _cells.Select(LineText));
    }

    private static string LineText(TerminalCell[] line) => string.Concat(line.Select(cell => cell.Text)).TrimEnd();

    private void WriteCharacter(char value)
    {
        if (_highSurrogate is { } high)
        {
            _highSurrogate = null;
            if (char.IsLowSurrogate(value)) { WriteRune(new Rune(high, value)); return; }
            WriteRune(Rune.ReplacementChar);
        }
        if (char.IsHighSurrogate(value)) _highSurrogate = value;
        else WriteRune(char.IsLowSurrogate(value) ? Rune.ReplacementChar : new Rune(value));
    }

    private void WriteRune(Rune rune)
    {
        var width = DisplayWidth(rune);
        if (width == 0)
        {
            var x = _wrapPending ? _column : _column - 1;
            if (x >= 0)
            {
                if (_cells[_row][x].Width == 0) x--;
                if (x >= 0) _cells[_row][x] = _cells[_row][x] with { Text = _cells[_row][x].Text + rune };
            }
            return;
        }
        if (_wrapPending && _autoWrap || width == 2 && _column == _columns - 1 && _autoWrap)
        {
            _column = 0;
            Index();
        }
        _wrapPending = false;
        if (width > _columns || width == 2 && _column == _columns - 1) { rune = Rune.ReplacementChar; width = 1; }
        ClearGlyph(_row, _column);
        if (width == 2) ClearGlyph(_row, _column + 1);
        _cells[_row][_column] = new(rune.ToString(), _reverse ? _background : _foreground,
            _reverse ? _foreground : _background, _bold, width);
        if (width == 2) _cells[_row][_column + 1] = new("", _foreground, _background, _bold, 0);
        _column += width;
        if (_column >= _columns) { _column = _columns - 1; _wrapPending = true; }
    }

    private static int DisplayWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        var c = rune.Value;
        if (c is >= 0x1100 and <= 0x115f or 0x2329 or 0x232a
            or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd) return 2;
        return 1;
    }

    private void ClearGlyph(int row, int column)
    {
        if (column < 0 || column >= _columns) return;
        if (_cells[row][column].Width == 0 && column > 0) _cells[row][column - 1] = Blank();
        if (_cells[row][column].Width == 2 && column + 1 < _columns) _cells[row][column + 1] = Blank();
        _cells[row][column] = Blank();
    }

    private TerminalCell Blank() => new(" ", _foreground, _background, false);
    private TerminalCell[][] CreateCells(int columns, int rows) =>
        Enumerable.Range(0, rows).Select(_ => Enumerable.Range(0, columns).Select(_ => Blank()).ToArray()).ToArray();

    internal static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be between 1 and 500.");
        if (rows is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(rows), "Rows must be between 1 and 300.");
    }

    private sealed record BufferState(TerminalCell[][] Cells, int Column, int Row, int Top, int Bottom);
    private enum ParseState { Ground, Escape, Csi, String, StringEscape, Charset }
}
