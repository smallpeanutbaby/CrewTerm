namespace CrewTerm.Terminal.Emulation;

/// <remarks>
/// 本分部文件：VT 输出状态机——转义序列、控制序列（CSI，含私有前缀分派）、字符串序列与 C0 控制字符的执行。调用方必须持有屏幕锁。
/// </remarks>
public sealed partial class TerminalScreen
{
    /// <summary>
    /// CSI 序列（不含引导符和结束字节）的最大长度；超过时放弃整个序列。
    /// </summary>
    private const int MaximumSequenceLength = 256;

    /// <summary>
    /// VT 状态机：消费一个输出字符。调用方必须持有 <c>_gate</c>。
    /// </summary>
    /// <param name="value">子程序输出的一个 UTF-16 字符。</param>
    private void Consume(char value)
    {
        if (_parseState == VtParseState.String)
        {
            if (value == '\a') _parseState = VtParseState.Ground;
            else if (value == '\u001b') _parseState = VtParseState.StringEscape;
            return;
        }
        if (_parseState == VtParseState.StringEscape)
        {
            _parseState = value == '\\' ? VtParseState.Ground : VtParseState.String;
            return;
        }
        if (value == '\u001b') { _parseState = VtParseState.Escape; _sequence.Clear(); _sequenceOverflow = false; return; }
        if (_parseState == VtParseState.Charset)
        {
            if (_charsetTarget == '(') _g0LineDrawing = value == '0';
            if (_charsetTarget == ')') _g1LineDrawing = value == '0';
            _parseState = VtParseState.Ground;
            return;
        }
        if (_parseState == VtParseState.Escape)
        {
            _parseState = VtParseState.Ground;
            switch (value)
            {
                case '[': _parseState = VtParseState.Csi; break;
                case ']': case 'P': case '_': case '^': _parseState = VtParseState.String; break;
                // 这些引导符后面固定再跟一个字节（字符集指定、DECALN、UTF-8 选择、7/8 位控制），一并吞掉，避免把它打印出来。
                case '(': case ')': case '*': case '+': case '#': case '%': case ' ':
                    _charsetTarget = value; _parseState = VtParseState.Charset; break;
                case 'c': ResetTerminal(); break;
                case '7':
                    _savedCursor = (_column, _row);
                    _savedRendition = new("", _foreground, _background, _bold, 1, _faint, _italic, _underline, _blink, _hidden, _strikethrough);
                    _savedReverse = _reverse;
                    break;
                case '8': RestoreCursor(true); break;
                case 'D': Index(); break;
                case 'E': _column = 0; Index(); break;
                case 'M': ReverseIndex(); break;
            }
            return;
        }
        if (_parseState == VtParseState.Csi)
        {
            if (value is '\u0018' or '\u001a') { _parseState = VtParseState.Ground; return; }
            // VT500 行为：CSI 序列中间出现的 C0 控制字符立即执行，序列继续。
            if (value < ' ') { ExecuteControl(value); return; }
            if (value == '\u007f') return;
            if (value is >= '@' and <= '~')
            {
                if (!_sequenceOverflow) ExecuteCsi(value, _sequence.ToString());
                _parseState = VtParseState.Ground;
            }
            else if (_sequence.Length < MaximumSequenceLength) _sequence.Append(value);
            // 超长的序列继续吞掉直到结束字节，既不执行，也不把剩余的参数字节打印到屏幕上。
            else _sequenceOverflow = true;
            return;
        }
        if (char.IsControl(value)) ExecuteControl(value);
        else WriteCharacter(value);
    }

    /// <summary>
    /// 执行 C0 控制字符；不支持的控制字符（以及 C1 控制字符）被忽略，绝不显示。
    /// </summary>
    /// <param name="value">控制字符。</param>
    private void ExecuteControl(char value)
    {
        switch (value)
        {
            case '\r': _column = 0; _wrapPending = false; break;
            case '\n': case '\v': case '\f': if (_newLineMode) _column = 0; Index(); break;
            case '\u000e': _useG1 = true; break;
            case '\u000f': _useG1 = false; break;
            case '\b': _column = Math.Max(0, _column - 1); _wrapPending = false; break;
            case '\t': _column = Math.Min(_columns - 1, (_column / 8 + 1) * 8); _wrapPending = false; break;
        }
    }

    /// <summary>
    /// 执行一个完整的 CSI 序列。
    /// </summary>
    /// <remarks>
    /// <para>带中间字节（0x20–0x2F，例如 DECSCUSR 的 <c>CSI 1 SP q</c>）的序列属于另一组命令，整体忽略。</para>
    /// <para>私有前缀决定命令含义：<c>?</c> 为 DEC 私有模式与查询；<c>&gt;</c>、<c>&lt;</c>、<c>=</c> 用于
    /// XTMODKEYS、kitty 键盘协议、DA2/DA3 等。带这些前缀的序列只执行明确支持的少数几个，
    /// 其余忽略，绝不能当作同名的标准命令（例如把 <c>CSI &gt; 1 u</c> 当作“恢复光标”，
    /// 把 <c>CSI &gt; 4;1 m</c> 当作“加粗”）。</para>
    /// </remarks>
    /// <param name="command">结束字节。</param>
    /// <param name="sequence">引导符与结束字节之间的全部字符（含私有前缀）。</param>
    private void ExecuteCsi(char command, string sequence)
    {
        if (sequence.Any(c => c is >= ' ' and <= '/')) return;
        var prefix = sequence.Length > 0 && sequence[0] is '<' or '=' or '>' or '?' ? sequence[0] : '\0';
        var parameters = prefix == '\0' ? sequence : sequence[1..];
        if (command == 'm')
        {
            if (prefix == '\0') ApplySgr(ParseSgr(parameters));
            return;
        }
        var values = parameters.Split(';').Select(p => int.TryParse(p, out var n) ? Math.Clamp(n, 0, 100000) : 0).ToArray();
        switch (prefix)
        {
            case '?': ExecutePrivateCsi(command, values); return;
            case '>':
                if (command == 'c') _responses.Add("\u001b[>0;10;1c");
                return;
            case '<': case '=': return;
        }
        ExecuteStandardCsi(command, values);
    }

    /// <summary>
    /// 执行带 <c>?</c> 前缀的 DEC 私有序列：模式设置/重置、kitty 键盘协议查询、DECXCPR、选择性擦除。
    /// </summary>
    /// <param name="command">结束字节。</param>
    /// <param name="values">数值参数（缺省或无效为 0）。</param>
    private void ExecutePrivateCsi(char command, int[] values)
    {
        int Param(int index, int fallback = 1) => index < values.Length && values[index] != 0 ? values[index] : fallback;
        switch (command)
        {
            case 'h': case 'l':
                _wrapPending = false;
                foreach (var value in values) SetMode(value, command == 'h');
                break;
            // kitty 键盘协议查询：回答“不支持任何增强标志”，应用会退回传统按键编码。
            case 'u': _responses.Add("\u001b[?0u"); break;
            case 'n':
                if (Param(0, 0) == 6) _responses.Add($"\u001b[?{_row - (_originMode ? _scrollTop : 0) + 1};{_column + 1}R");
                else if (Param(0, 0) == 5) _responses.Add("\u001b[0n");
                break;
            // DECSED / DECSEL：本模型没有“受保护字符”，与普通擦除等价。
            case 'J': _wrapPending = false; EraseDisplay(Param(0, 0)); break;
            case 'K': _wrapPending = false; EraseLine(Param(0, 0)); break;
        }
    }

    /// <summary>
    /// 执行不带前缀的标准 CSI 命令（光标移动、擦除、滚动区域、插入删除、设备状态报告等）。
    /// </summary>
    /// <param name="command">结束字节。</param>
    /// <param name="values">数值参数（缺省或无效为 0）。</param>
    private void ExecuteStandardCsi(char command, int[] values)
    {
        int Param(int index, int fallback = 1) => index < values.Length && values[index] != 0 ? values[index] : fallback;
        var topBound = _originMode ? _scrollTop : 0;
        var bottomBound = _originMode ? _scrollBottom : _rows - 1;
        _wrapPending = false;
        switch (command)
        {
            case 'H': case 'f':
                _row = Math.Clamp(Param(0) - 1 + topBound, topBound, bottomBound);
                _column = Math.Clamp(Param(1) - 1, 0, _columns - 1);
                break;
            case 'A': _row = Math.Max(topBound, _row - Param(0)); break;
            case 'B': case 'e': _row = Math.Min(bottomBound, _row + Param(0)); break;
            case 'C': case 'a': _column = Math.Min(_columns - 1, _column + Param(0)); break;
            case 'D': _column = Math.Max(0, _column - Param(0)); break;
            case 'E': _row = Math.Min(bottomBound, _row + Param(0)); _column = 0; break;
            case 'F': _row = Math.Max(topBound, _row - Param(0)); _column = 0; break;
            case 'G': case '`': _column = Math.Clamp(Param(0) - 1, 0, _columns - 1); break;
            case 'd': _row = Math.Clamp(Param(0) - 1 + topBound, topBound, bottomBound); break;
            case 'K': EraseLine(Param(0, 0)); break;
            case 'J': EraseDisplay(Param(0, 0)); break;
            case 'h': case 'l':
                foreach (var value in values)
                {
                    if (value == 4) _insertMode = command == 'h';
                    if (value == 20) _newLineMode = command == 'h';
                }
                break;
            case 'r':
                var top = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                var bottom = Math.Clamp(Param(1, _rows) - 1, 0, _rows - 1);
                if (top < bottom) { _scrollTop = top; _scrollBottom = bottom; _row = _originMode ? top : 0; _column = 0; }
                break;
            case 's': _savedCursor = (_column, _row); break;
            case 'u': RestoreCursor(); break;
            case 'n':
                if (Param(0, 0) == 6) _responses.Add($"\u001b[{_row - topBound + 1};{_column + 1}R");
                else if (Param(0, 0) == 5) _responses.Add("\u001b[0n");
                break;
            case 'c': _responses.Add("\u001b[?1;2c"); break;
            case 'P': DeleteCharacters(Param(0)); break;
            case '@': InsertCharacters(Param(0)); break;
            case 'X': EraseCells(_row, _column, Math.Min(_columns - 1, _column + Param(0) - 1)); break;
            case 'S': ScrollUp(_scrollTop, _scrollBottom, Param(0)); break;
            case 'T': ScrollDown(_scrollTop, _scrollBottom, Param(0)); break;
            case 'L': if (_row >= _scrollTop && _row <= _scrollBottom) ScrollDown(_row, _scrollBottom, Param(0)); break;
            case 'M': if (_row >= _scrollTop && _row <= _scrollBottom) ScrollUp(_row, _scrollBottom, Param(0), false); break;
        }
    }

    /// <summary>
    /// 恢复 <c>ESC 7</c> / <c>CSI s</c> 保存的光标位置，可选同时恢复图形属性。
    /// </summary>
    /// <param name="restoreRendition">为 <see langword="true"/> 时（<c>ESC 8</c>）同时恢复颜色与样式。</param>
    private void RestoreCursor(bool restoreRendition = false)
    {
        _column = Math.Clamp(_savedCursor.Column, 0, _columns - 1);
        _row = Math.Clamp(_savedCursor.Row, 0, _rows - 1);
        _wrapPending = false;
        if (restoreRendition && _savedRendition is { } style)
        {
            _foreground = style.Foreground;
            _background = style.Background;
            _bold = style.Bold;
            _faint = style.Faint;
            _italic = style.Italic;
            _underline = style.Underline;
            _blink = style.Blink;
            _hidden = style.Hidden;
            _strikethrough = style.Strikethrough;
            _reverse = _savedReverse;
        }
    }
}
