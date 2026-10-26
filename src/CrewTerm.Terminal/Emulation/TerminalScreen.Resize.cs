namespace CrewTerm.Terminal.Emulation;

/// <remarks>
/// 本分部文件：调整屏幕尺寸，按新尺寸重建主屏幕与备用屏幕缓冲区，必要时把移出顶部的行写入滚动历史。
/// </remarks>
public sealed partial class TerminalScreen
{
    /// <summary>
    /// 改变屏幕尺寸，重建活动缓冲区以及（处于备用屏幕时）保存的主屏幕。
    /// </summary>
    /// <remarks>
    /// 内容不重排（不做 reflow）：每行保留左侧不超过新列数的部分，断开的宽字符被清除。
    /// 行数不足以容纳光标所在行时，从顶部移出多余的行使光标行成为末行；当前缓冲区是主屏幕时移出的行写入滚动历史。
    /// 光标被限制在新尺寸内，滚动区域恢复为整屏，延迟换行标志被清除。尺寸不变时什么也不做。可以从任意线程调用。
    /// </remarks>
    /// <param name="columns">新列数（1–500）。</param>
    /// <param name="rows">新行数（1–300）。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸超出允许范围。</exception>
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

    /// <summary>
    /// 按新尺寸重建一块缓冲区。
    /// </summary>
    /// <remarks>
    /// 光标行超出新行数时，从顶部移出 <c>光标行 - 新行数 + 1</c> 行，保证光标所在行仍可见；
    /// 其余行从上往下复制、超出新列数的部分截掉，新增的行列以当前颜色的空白填充。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="source">原缓冲区状态。</param>
    /// <param name="columns">新列数。</param>
    /// <param name="rows">新行数。</param>
    /// <param name="history">是否把从顶部移出的行写入滚动历史（只对主屏幕为 <see langword="true"/>）。</param>
    /// <returns>新缓冲区：光标被限制在新尺寸内，滚动区域为整屏。</returns>
    private ScreenBufferState ResizeBuffer(ScreenBufferState source, int columns, int rows, bool history)
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
