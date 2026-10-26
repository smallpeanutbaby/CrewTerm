namespace CrewTerm.Terminal;

public sealed partial class TerminalScreen
{
    private void Index()
    {
        _wrapPending = false;
        if (_row == _scrollBottom) ScrollUp(_scrollTop, _scrollBottom, 1);
        else _row = Math.Min(_rows - 1, _row + 1);
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_row == _scrollTop) ScrollDown(_scrollTop, _scrollBottom, 1);
        else _row = Math.Max(0, _row - 1);
    }

    private void ScrollUp(int top, int bottom, int count, bool history = true)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var n = 0; n < count; n++)
        {
            if (history && top == 0 && bottom == _rows - 1 && !_alternate)
            {
                _history.Enqueue(LineText(_cells[top]));
                if (_history.Count > 2000) _history.Dequeue();
            }
            for (var row = top; row < bottom; row++) _cells[row] = _cells[row + 1];
            _cells[bottom] = CreateCells(_columns, 1)[0];
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var n = 0; n < count; n++)
        {
            for (var row = bottom; row > top; row--) _cells[row] = _cells[row - 1];
            _cells[top] = CreateCells(_columns, 1)[0];
        }
    }

    private void EraseLine(int mode)
    {
        if (mode is < 0 or > 2) return;
        EraseCells(_row, mode is 1 or 2 ? 0 : _column, mode is 0 or 2 ? _columns - 1 : _column);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var row = _row + 1; row < _rows; row++) EraseCells(row, 0, _columns - 1);
                break;
            case 1:
                EraseLine(1);
                for (var row = 0; row < _row; row++) EraseCells(row, 0, _columns - 1);
                break;
            case 2: _cells = CreateCells(_columns, _rows); break;
            case 3: _history.Clear(); break;
        }
    }

    private void EraseCells(int row, int from, int to)
    {
        for (var x = from; x <= to; x++) ClearGlyph(row, x);
    }

    private void DeleteCharacters(int count)
    {
        count = Math.Min(count, _columns - _column);
        for (var x = _column; x < _columns - count; x++) _cells[_row][x] = _cells[_row][x + count];
        for (var x = _columns - count; x < _columns; x++) _cells[_row][x] = Blank();
        NormalizeLine(_cells[_row]);
    }

    private void InsertCharacters(int count)
    {
        count = Math.Min(count, _columns - _column);
        for (var x = _columns - 1; x >= _column + count; x--) _cells[_row][x] = _cells[_row][x - count];
        for (var x = _column; x < _column + count; x++) _cells[_row][x] = Blank();
        NormalizeLine(_cells[_row]);
    }

    private void NormalizeLine(TerminalCell[] line)
    {
        for (var x = 0; x < line.Length; x++)
        {
            if (line[x].Width == 2 && (x + 1 == line.Length || line[x + 1].Width != 0)) line[x] = Blank();
            if (line[x].Width == 0 && (x == 0 || line[x - 1].Width != 2)) line[x] = Blank();
        }
    }

    private void SetMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 1: _applicationCursor = enabled; break;
            case 7: _autoWrap = enabled; break;
            case 25: _cursorVisible = enabled; break;
            case 47: case 1047: case 1049: SwitchAlternate(enabled); break;
            case 2004: _bracketedPaste = enabled; break;
            case 1000: case 1002: case 1003: _mouseTracking = enabled ? mode : 0; break;
            case 1006: _sgrMouse = enabled; break;
        }
    }

    private void SwitchAlternate(bool enabled)
    {
        if (enabled == _alternate) return;
        if (enabled)
        {
            _primary = new(_cells, _column, _row, _scrollTop, _scrollBottom);
            _cells = CreateCells(_columns, _rows);
            _row = _column = _scrollTop = 0;
            _scrollBottom = _rows - 1;
        }
        else if (_primary is { } primary)
        {
            _cells = primary.Cells;
            _column = primary.Column;
            _row = primary.Row;
            _scrollTop = primary.Top;
            _scrollBottom = primary.Bottom;
            _primary = null;
        }
        _alternate = enabled;
        _wrapPending = false;
    }
}
