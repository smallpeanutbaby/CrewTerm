namespace CrewTerm.Terminal.Emulation;

/// <remarks>
/// 本分部文件：换行与反向换行、区域滚动与滚动历史、擦除、插入与删除字符、模式设置、终端复位与主/备用屏幕切换。调用方必须持有屏幕锁。
/// </remarks>
public sealed partial class TerminalScreen
{
    /// <summary>
    /// 光标下移一行（IND，也用于 LF/VT/FF、<c>ESC D</c>、<c>ESC E</c> 和自动换行）。
    /// </summary>
    /// <remarks>
    /// 光标位于滚动区域末行时，滚动区域向上滚动一行、光标不动；否则光标下移一行，但不超过屏幕末行
    /// （光标在滚动区域下方时不会触发滚动）。同时清除延迟换行标志。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    private void Index()
    {
        _wrapPending = false;
        if (_row == _scrollBottom) ScrollUp(_scrollTop, _scrollBottom, 1);
        else _row = Math.Min(_rows - 1, _row + 1);
    }

    /// <summary>
    /// 光标上移一行（RI，<c>ESC M</c>）。
    /// </summary>
    /// <remarks>
    /// 光标位于滚动区域首行时，滚动区域向下滚动一行、光标不动；否则光标上移一行，但不超过屏幕首行。
    /// 同时清除延迟换行标志。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_row == _scrollTop) ScrollDown(_scrollTop, _scrollBottom, 1);
        else _row = Math.Max(0, _row - 1);
    }

    /// <summary>
    /// 把 <paramref name="top"/>–<paramref name="bottom"/> 之间的行向上滚动，底部补入空白行；光标不动。
    /// </summary>
    /// <remarks>
    /// 滚出的行在 <paramref name="history"/> 为 <see langword="true"/>、区域覆盖整屏且处于主屏幕时写入滚动历史，
    /// 历史超过 2,000 行时丢弃最旧的一行。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="top">区域首行（从 0 开始，含）。</param>
    /// <param name="bottom">区域末行（从 0 开始，含）。</param>
    /// <param name="count">滚动行数，超过区域高度时按区域高度处理。</param>
    /// <param name="history">是否允许把滚出的行记入历史；删除行（<c>CSI M</c>）传 <see langword="false"/>。</param>
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

    /// <summary>
    /// 把 <paramref name="top"/>–<paramref name="bottom"/> 之间的行向下滚动，顶部补入空白行，底部的行被丢弃；光标不动。
    /// </summary>
    /// <remarks>
    /// 用于 RI、<c>CSI T</c>（向下滚动）与 <c>CSI L</c>（插入行）。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="top">区域首行（从 0 开始，含）。</param>
    /// <param name="bottom">区域末行（从 0 开始，含）。</param>
    /// <param name="count">滚动行数，超过区域高度时按区域高度处理。</param>
    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var n = 0; n < count; n++)
        {
            for (var row = bottom; row > top; row--) _cells[row] = _cells[row - 1];
            _cells[top] = CreateCells(_columns, 1)[0];
        }
    }

    /// <summary>
    /// 擦除光标所在行的一部分（EL，<c>CSI Ps K</c>）；光标不动。
    /// </summary>
    /// <param name="mode">0：光标到行尾；1：行首到光标；2：整行（均含光标所在格）。其他值忽略。</param>
    private void EraseLine(int mode)
    {
        if (mode is < 0 or > 2) return;
        EraseCells(_row, mode is 1 or 2 ? 0 : _column, mode is 0 or 2 ? _columns - 1 : _column);
    }

    /// <summary>
    /// 擦除屏幕的一部分（ED，<c>CSI Ps J</c>）；光标不动。
    /// </summary>
    /// <param name="mode">
    /// 0：光标到屏幕末尾；1：屏幕开头到光标；2：整屏（以当前颜色的空白重建单元格数组）；
    /// 3：只清空滚动历史，不擦除屏幕。其他值忽略。
    /// </param>
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

    /// <summary>
    /// 把一行中的一段单元格清为空白；与区间边界相交的宽字符会被整个清除。
    /// </summary>
    /// <param name="row">行（从 0 开始）。</param>
    /// <param name="from">起始列（从 0 开始，含）。</param>
    /// <param name="to">结束列（从 0 开始，含）；小于 <paramref name="from"/> 时什么也不做。</param>
    private void EraseCells(int row, int from, int to)
    {
        for (var x = from; x <= to; x++) ClearGlyph(row, x);
    }

    /// <summary>
    /// 删除光标处起的若干字符（DCH，<c>CSI Ps P</c>）：右侧内容左移，行尾补空白；光标不动。
    /// </summary>
    /// <param name="count">删除的单元格数，超过光标到行尾的距离时按该距离处理。</param>
    private void DeleteCharacters(int count)
    {
        count = Math.Min(count, _columns - _column);
        for (var x = _column; x < _columns - count; x++) _cells[_row][x] = _cells[_row][x + count];
        for (var x = _columns - count; x < _columns; x++) _cells[_row][x] = Blank();
        NormalizeLine(_cells[_row]);
    }

    /// <summary>
    /// 在光标处插入若干空白（ICH，<c>CSI Ps @</c>，也用于插入模式下的写入）：右侧内容右移，挤出行尾的内容被丢弃；光标不动。
    /// </summary>
    /// <param name="count">插入的单元格数，超过光标到行尾的距离时按该距离处理。</param>
    private void InsertCharacters(int count)
    {
        count = Math.Min(count, _columns - _column);
        for (var x = _columns - 1; x >= _column + count; x--) _cells[_row][x] = _cells[_row][x - count];
        for (var x = _column; x < _column + count; x++) _cells[_row][x] = Blank();
        NormalizeLine(_cells[_row]);
    }

    /// <summary>
    /// 修复因单元格移动或截断而断开的宽字符：后面没有续位单元格的宽字符首格，
    /// 以及前面不是宽字符首格的续位单元格，都替换为空白。
    /// </summary>
    /// <param name="line">要原地修复的一行单元格。</param>
    private void NormalizeLine(TerminalCell[] line)
    {
        for (var x = 0; x < line.Length; x++)
        {
            if (line[x].Width == 2 && (x + 1 == line.Length || line[x + 1].Width != 0)) line[x] = Blank();
            if (line[x].Width == 0 && (x == 0 || line[x - 1].Width != 2)) line[x] = Blank();
        }
    }

    /// <summary>
    /// 设置或重置一个 DEC 私有模式（<c>CSI ? Pm h</c> / <c>CSI ? Pm l</c>）。
    /// </summary>
    /// <remarks>
    /// 支持：1 应用光标键（DECCKM）；6 原点模式（DECOM，同时把光标移到滚动区域或屏幕左上角）；7 自动换行（DECAWM）；
    /// 25 光标可见（DECTCEM）；47/1047/1049 备用屏幕（三者行为相同）；2004 括号粘贴；
    /// 1000/1002/1003 鼠标跟踪（启用时记录模式号，重置其中任何一个都会关闭鼠标跟踪）；1005 UTF-8 鼠标编码；1006 SGR 鼠标编码。
    /// 其他模式忽略。
    /// </remarks>
    /// <param name="mode">模式号。</param>
    /// <param name="enabled"><see langword="true"/> 为设置（<c>h</c>），<see langword="false"/> 为重置（<c>l</c>）。</param>
    private void SetMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 1: _applicationCursor = enabled; break;
            case 6: _originMode = enabled; _row = enabled ? _scrollTop : 0; _column = 0; break;
            case 7: _autoWrap = enabled; break;
            case 25: _cursorVisible = enabled; break;
            case 47: case 1047: case 1049: SwitchAlternate(enabled); break;
            case 2004: _bracketedPaste = enabled; break;
            case 1000: case 1002: case 1003: _mouseTracking = enabled ? mode : 0; break;
            case 1005: _utf8Mouse = enabled; break;
            case 1006: _sgrMouse = enabled; break;
        }
    }

    /// <summary>
    /// 完全重置终端（RIS，<c>ESC c</c>）。
    /// </summary>
    /// <remarks>
    /// 恢复默认图形属性，清空滚动历史，丢弃备用屏幕保存的主屏幕并回到主屏幕，以当前（已恢复默认的）颜色重建空白单元格；
    /// 光标回到左上角并显示，滚动区域恢复为整屏，关闭鼠标跟踪、括号粘贴、应用光标键、原点、插入、换行等模式，
    /// 开启自动换行，字符集恢复为 ASCII，清除待配对的代理项与保存的光标。屏幕尺寸保持不变。
    /// </remarks>
    private void ResetTerminal()
    {
        ResetStyles();
        _history.Clear();
        _primary = null;
        _row = _column = _scrollTop = _mouseTracking = 0;
        _scrollBottom = _rows - 1;
        _cursorVisible = _autoWrap = true;
        _alternate = _bracketedPaste = _applicationCursor = _sgrMouse = _utf8Mouse = _originMode = _insertMode = _newLineMode = false;
        _g0LineDrawing = _g1LineDrawing = _useG1 = _wrapPending = false;
        _highSurrogate = null;
        _savedCursor = (0, 0);
        _savedRendition = null;
        _cells = CreateCells(_columns, _rows);
    }

    /// <summary>
    /// 在主屏幕与备用屏幕之间切换。
    /// </summary>
    /// <remarks>
    /// 进入备用屏幕时保存主屏幕的单元格、光标与滚动区域，换上一块全新的空白缓冲区，光标回到左上角、滚动区域为整屏；
    /// 退出时原样恢复主屏幕，备用屏幕内容被丢弃。目标状态与当前相同时什么也不做。
    /// 备用屏幕上的滚动不会写入历史。
    /// </remarks>
    /// <param name="enabled"><see langword="true"/> 进入备用屏幕，<see langword="false"/> 回到主屏幕。</param>
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
