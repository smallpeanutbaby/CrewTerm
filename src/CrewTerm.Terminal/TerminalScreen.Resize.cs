namespace CrewTerm.Terminal;

public sealed partial class TerminalScreen
{
    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        lock (_gate)
        {
            if (columns == _columns && rows == _rows) return;
            var resized = ResizeBuffer(new(_cells, _column, _row, _scrollTop, _scrollBottom), columns, rows, !_alternate);
            _cells = resized.Cells;
            _column = resized.Column;
            _row = resized.Row;
            if (_primary is not null) _primary = ResizeBuffer(_primary, columns, rows, false);
            _columns = columns;
            _rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _wrapPending = false;
        }
    }

    private BufferState ResizeBuffer(BufferState source, int columns, int rows, bool history)
    {
        var cells = CreateCells(columns, rows);
        var offset = Math.Max(0, source.Row - rows + 1);
        if (history)
            for (var i = 0; i < offset; i++)
            {
                _history.Enqueue(LineText(source.Cells[i]));
                if (_history.Count > 2000) _history.Dequeue();
            }
        for (var y = 0; y < Math.Min(rows, source.Cells.Length - offset); y++)
        {
            Array.Copy(source.Cells[y + offset], cells[y], Math.Min(columns, source.Cells[y + offset].Length));
            NormalizeLine(cells[y]);
        }
        return new(cells, Math.Clamp(source.Column, 0, columns - 1), Math.Clamp(source.Row - offset, 0, rows - 1), 0, rows - 1);
    }
}
