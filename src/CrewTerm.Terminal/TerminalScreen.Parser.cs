namespace CrewTerm.Terminal;

public sealed partial class TerminalScreen
{
    private void Consume(char value)
    {
        if (_parseState == ParseState.String)
        {
            if (value == '\a') _parseState = ParseState.Ground;
            else if (value == '\u001b') _parseState = ParseState.StringEscape;
            return;
        }
        if (_parseState == ParseState.StringEscape)
        {
            _parseState = value == '\\' ? ParseState.Ground : ParseState.String;
            return;
        }
        if (value == '\u001b') { _parseState = ParseState.Escape; _sequence.Clear(); return; }
        if (_parseState == ParseState.Charset) { _parseState = ParseState.Ground; return; }
        if (_parseState == ParseState.Escape)
        {
            _parseState = ParseState.Ground;
            switch (value)
            {
                case '[': _parseState = ParseState.Csi; break;
                case ']': case 'P': case '_': case '^': _parseState = ParseState.String; break;
                case '(': case ')': case '*': case '+': case '#': _parseState = ParseState.Charset; break;
                case '7': _savedCursor = (_column, _row); break;
                case '8': RestoreCursor(); break;
                case 'D': Index(); break;
                case 'E': _column = 0; Index(); break;
                case 'M': ReverseIndex(); break;
            }
            return;
        }
        if (_parseState == ParseState.Csi)
        {
            if (value is '\u0018' or '\u001a') { _parseState = ParseState.Ground; return; }
            if (value is >= '@' and <= '~')
            {
                ExecuteCsi(value, _sequence.ToString());
                _parseState = ParseState.Ground;
            }
            else if (_sequence.Length < 256) _sequence.Append(value);
            else _parseState = ParseState.Ground;
            return;
        }
        switch (value)
        {
            case '\r': _column = 0; _wrapPending = false; break;
            case '\n': case '\v': case '\f': Index(); break;
            case '\b': _column = Math.Max(0, _column - 1); _wrapPending = false; break;
            case '\t': _column = Math.Min(_columns - 1, (_column / 8 + 1) * 8); _wrapPending = false; break;
            default:
                if (!char.IsControl(value)) WriteCharacter(value);
                break;
        }
    }

    private void ExecuteCsi(char command, string parameters)
    {
        var privateMode = parameters.StartsWith('?');
        var parts = parameters.TrimStart('?', '>', '=').Split(';');
        var values = parts.Select(p => int.TryParse(p, out var n) ? Math.Clamp(n, 0, 100000) : 0).ToArray();
        int Param(int index, int fallback = 1) => index < values.Length && values[index] != 0 ? values[index] : fallback;
        if (command != 'm') _wrapPending = false;
        switch (command)
        {
            case 'H': case 'f':
                _row = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                _column = Math.Clamp(Param(1) - 1, 0, _columns - 1);
                break;
            case 'A': _row = Math.Max(0, _row - Param(0)); break;
            case 'B': case 'e': _row = Math.Min(_rows - 1, _row + Param(0)); break;
            case 'C': case 'a': _column = Math.Min(_columns - 1, _column + Param(0)); break;
            case 'D': _column = Math.Max(0, _column - Param(0)); break;
            case 'E': _row = Math.Min(_rows - 1, _row + Param(0)); _column = 0; break;
            case 'F': _row = Math.Max(0, _row - Param(0)); _column = 0; break;
            case 'G': case '`': _column = Math.Clamp(Param(0) - 1, 0, _columns - 1); break;
            case 'd': _row = Math.Clamp(Param(0) - 1, 0, _rows - 1); break;
            case 'K': EraseLine(Param(0, 0)); break;
            case 'J': EraseDisplay(Param(0, 0)); break;
            case 'm': ApplySgr(values); break;
            case 'h': case 'l':
                if (privateMode) foreach (var value in values) SetMode(value, command == 'h');
                break;
            case 'r':
                var top = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                var bottom = Math.Clamp(Param(1, _rows) - 1, 0, _rows - 1);
                if (top < bottom) { _scrollTop = top; _scrollBottom = bottom; _row = _column = 0; }
                break;
            case 's': _savedCursor = (_column, _row); break;
            case 'u':
                if (privateMode) _responses.Add("\u001b[?0u");
                else RestoreCursor();
                break;
            case 'n':
                if (Param(0, 0) == 6) _responses.Add($"\u001b[{(privateMode ? "?" : "")}{_row + 1};{_column + 1}R");
                else if (Param(0, 0) == 5) _responses.Add("\u001b[0n");
                break;
            case 'c': _responses.Add(parameters.StartsWith('>') ? "\u001b[>0;10;1c" : "\u001b[?1;2c"); break;
            case 'P': DeleteCharacters(Param(0)); break;
            case '@': InsertCharacters(Param(0)); break;
            case 'X': EraseCells(_row, _column, Math.Min(_columns - 1, _column + Param(0) - 1)); break;
            case 'S': ScrollUp(_scrollTop, _scrollBottom, Param(0)); break;
            case 'T': ScrollDown(_scrollTop, _scrollBottom, Param(0)); break;
            case 'L': if (_row >= _scrollTop && _row <= _scrollBottom) ScrollDown(_row, _scrollBottom, Param(0)); break;
            case 'M': if (_row >= _scrollTop && _row <= _scrollBottom) ScrollUp(_row, _scrollBottom, Param(0), false); break;
        }
    }

    private void RestoreCursor()
    {
        _column = Math.Clamp(_savedCursor.Column, 0, _columns - 1);
        _row = Math.Clamp(_savedCursor.Row, 0, _rows - 1);
        _wrapPending = false;
    }
}
