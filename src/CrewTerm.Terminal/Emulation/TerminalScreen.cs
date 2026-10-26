using System.Globalization;
using System.Text;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.Terminal.Emulation;

/// <summary>
/// 增量更新的字符单元格终端状态（VT 屏幕模型），与任何 UI 和原生句柄无关。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>子程序输出经 <see cref="Feed"/> 逐字符送入 VT 状态机，更新单元格、光标、滚动区域、图形属性与各种 DEC 模式。</description></item>
/// <item><description>维护主屏幕与备用屏幕两块缓冲区；只有主屏幕整屏向上滚动出去的行进入滚动历史（最多 2,000 行，只保存去掉行尾空白的文本）。</description></item>
/// <item><description>全部状态由 <c>_gate</c> 保护，公共方法可以从任意线程调用；所有私有方法都要求调用方已持有 <c>_gate</c>。</description></item>
/// <item><description>终端查询（DSR、DA 等）的应答先收集起来，在释放锁之后通过 <see cref="ResponseRequested"/> 发出，避免与原生 resize 发生锁顺序反转。</description></item>
/// </list>
/// 坐标一律从 0 开始，单位为单元格；尺寸限制为 1–500 列、1–300 行。
/// </remarks>
public sealed partial class TerminalScreen
{
    /// <summary>
    /// 默认前景色，24 位 RGB（<c>0xRRGGBB</c>）。初始状态、SGR 0 与 SGR 39 使用该颜色。
    /// </summary>
    public const int DefaultForeground = 0xD0D7DE;

    /// <summary>
    /// 默认背景色，24 位 RGB（<c>0xRRGGBB</c>）。初始状态、SGR 0 与 SGR 49 使用该颜色。
    /// </summary>
    public const int DefaultBackground = 0x0D1117;

    /// <summary>
    /// 保护本类全部可变状态的锁。公共方法在持锁时读写状态，私有方法都假定调用方已持有它。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 正在收集的 CSI 序列内容：<c>ESC [</c> 之后、结束字节之前的全部字符（含私有前缀），
    /// 最多 <see cref="MaximumSequenceLength"/> 个；每次读到 <c>ESC</c> 时清空。
    /// </summary>
    private readonly StringBuilder _sequence = new();

    /// <summary>
    /// 当前 CSI 序列是否已超过 <see cref="MaximumSequenceLength"/>：超长的序列继续被吞掉直到结束字节，但不执行，
    /// 剩余的参数字节也不会被当作文本打印。每次读到 <c>ESC</c> 时重置。
    /// </summary>
    private bool _sequenceOverflow;

    /// <summary>
    /// 一个单元格中字形簇（基础字符加组合符、变体选择符与 ZWJ 连接的字符）的最大 UTF-16 长度；
    /// 足以容纳最长的标准 emoji 序列，超出部分被丢弃。
    /// </summary>
    private const int MaximumClusterLength = 64;

    /// <summary>
    /// 滚动历史：从主屏幕顶部滚出的行文本（已去掉行尾空白），队首最旧，最多保留 2,000 行。
    /// </summary>
    /// <remarks>
    /// 只记录滚动区域覆盖整屏时的向上滚动，以及 resize 时为保住光标所在行而从主屏幕顶部移出的行；
    /// 备用屏幕、局部滚动区域以及删除行（<c>CSI M</c>）滚出的行不记录。<c>CSI 3 J</c> 与 <c>ESC c</c> 会清空历史。
    /// </remarks>
    private readonly Queue<string> _history = new();

    /// <summary>
    /// 本次 <see cref="Feed"/> 解析期间产生、尚未发出的查询应答（完整转义序列）。
    /// </summary>
    private readonly List<string> _responses = [];

    /// <summary>
    /// 当前活动缓冲区（主屏幕或备用屏幕）的单元格，按 <c>[行][列]</c> 索引。
    /// </summary>
    /// <remarks>
    /// 滚动通过移动行数组引用完成；切换缓冲区、整屏擦除、重置和 resize 时整体替换为新数组。
    /// </remarks>
    private TerminalCell[][] _cells;

    /// <summary>
    /// 屏幕列数与行数（单元格）、光标所在列与行（从 0 开始），以及滚动区域（DECSTBM）的首行与末行（从 0 开始，均含）。
    /// </summary>
    private int _columns, _rows, _column, _row, _scrollTop, _scrollBottom;

    /// <summary>
    /// 当前图形属性中的前景色与背景色（24 位 RGB）。写入字符和擦除单元格时使用。
    /// </summary>
    private int _foreground = DefaultForeground, _background = DefaultBackground;

    /// <summary>
    /// 加粗（SGR 1）；反显（SGR 7，写入字符时交换前景色与背景色）；
    /// 延迟换行标志（已写到最后一列，光标停在该列，下一个可打印字符先换行再写入）；
    /// 自动换行模式（DECAWM，<c>CSI ? 7 h/l</c>，默认开启）。
    /// </summary>
    private bool _bold, _reverse, _wrapPending, _autoWrap = true;

    /// <summary>
    /// 文字样式：暗淡（SGR 2）、斜体（SGR 3）、下划线（SGR 4/21）、闪烁（SGR 5/6）、隐藏（SGR 8）、删除线（SGR 9）。
    /// </summary>
    private bool _faint, _italic, _underline, _blink, _hidden, _strikethrough;

    /// <summary>
    /// 原点模式（DECOM，<c>CSI ? 6</c>：行坐标相对滚动区域并被限制在其内）；
    /// 插入模式（IRM，<c>CSI 4 h</c>：写入前把光标及右侧内容右移）；
    /// 换行模式（LNM，<c>CSI 20 h</c>：LF/VT/FF 同时回到行首）；
    /// G0、G1 是否指定为 DEC 特殊图形（线条绘制）字符集（<c>ESC ( 0</c>、<c>ESC ) 0</c>）；
    /// 当前是否使用 G1（SO 选中 G1，SI 切回 G0）。
    /// </summary>
    private bool _originMode, _insertMode, _newLineMode, _g0LineDrawing, _g1LineDrawing, _useG1;

    /// <summary>
    /// 两字节转义序列的引导符（<c>(</c>、<c>)</c>、<c>*</c>、<c>+</c>、<c>#</c>、<c>%</c> 或空格），
    /// 在 <see cref="VtParseState.Charset"/> 状态下决定最后一个字节的含义；只有 <c>(</c> 与 <c>)</c> 会产生效果。
    /// </summary>
    private char _charsetTarget;

    /// <summary>
    /// 光标是否可见（DECTCEM，<c>CSI ? 25</c>）；是否处于备用屏幕（<c>CSI ? 47/1047/1049</c>）；
    /// 括号粘贴模式（<c>CSI ? 2004</c>）；应用光标键模式（DECCKM，<c>CSI ? 1</c>）；
    /// SGR 鼠标编码（<c>CSI ? 1006</c>）；UTF-8 鼠标编码（<c>CSI ? 1005</c>）。
    /// 这些模式随快照交给 UI，用于决定按键、粘贴与鼠标事件的编码方式。
    /// </summary>
    private bool _cursorVisible = true, _alternate, _bracketedPaste, _applicationCursor, _sgrMouse, _utf8Mouse;

    /// <summary>
    /// 鼠标跟踪模式：0 表示关闭；否则为启用的 DEC 私有模式号——1000（仅按键）、1002（按键与拖动）或 1003（所有移动）。
    /// </summary>
    private int _mouseTracking;

    /// <summary>
    /// 尚未配对的 UTF-16 高代理项。输出分块送入时，一个代理对可能跨越两次 <see cref="Feed"/>。
    /// </summary>
    private char? _highSurrogate;

    /// <summary>
    /// VT 解析状态机的当前状态。
    /// </summary>
    private VtParseState _parseState;

    /// <summary>
    /// <c>ESC 7</c> 或 <c>CSI s</c> 保存的光标位置（从 0 开始的列与行）。
    /// </summary>
    private (int Column, int Row) _savedCursor;

    /// <summary>
    /// <c>ESC 7</c> 保存的图形属性，借用 <see cref="TerminalCell"/> 承载颜色与样式（文本为空）；
    /// 从未保存过或已重置时为 <see langword="null"/>。
    /// </summary>
    private TerminalCell? _savedRendition;

    /// <summary>
    /// <c>ESC 7</c> 保存的反显状态（<see cref="TerminalCell"/> 没有反显字段，因此单独保存）。
    /// </summary>
    private bool _savedReverse;

    /// <summary>
    /// 处于备用屏幕时保存的主屏幕缓冲区；处于主屏幕时为 <see langword="null"/>。
    /// </summary>
    private ScreenBufferState? _primary;

    /// <summary>
    /// 需要向子程序回复终端查询时触发；参数是要写入子程序输入的完整转义序列。
    /// </summary>
    /// <remarks>
    /// 应答对象包括设备状态报告（<c>CSI 5 n</c>、<c>CSI 6 n</c> 及其 DEC 私有形式）、主/次设备属性（<c>CSI c</c>、
    /// <c>CSI &gt; c</c>）与 kitty 键盘协议查询（<c>CSI ? u</c>）。在调用 <see cref="Feed"/> 的线程上、
    /// 释放屏幕锁之后按产生顺序触发。
    /// </remarks>
    public event Action<string>? ResponseRequested;

    /// <summary>
    /// 创建指定尺寸的空白屏幕：光标位于左上角，滚动区域为整屏，图形属性为默认值。
    /// </summary>
    /// <param name="columns">列数（1–500）。</param>
    /// <param name="rows">行数（1–300）。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸超出允许范围。</exception>
    public TerminalScreen(int columns, int rows)
    {
        ValidateSize(columns, rows);
        _columns = columns;
        _rows = rows;
        _scrollBottom = rows - 1;
        _cells = CreateCells(columns, rows);
    }

    /// <summary>
    /// 把子程序的输出文本送入 VT 状态机并更新屏幕。
    /// </summary>
    /// <remarks>
    /// 可以从任意线程调用。文本可以在任意位置截断：未完成的转义序列与代理对会保留到下一次调用继续解析。
    /// 解析期间产生的查询应答在释放屏幕锁后依次通过 <see cref="ResponseRequested"/> 发出。
    /// </remarks>
    /// <param name="text">已解码的输出文本。</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/>。</exception>
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
        // 应答会写入原生输入管道。在屏幕锁之外调用，避免与同时更新本屏幕的原生 resize
        // 发生锁顺序反转。
        foreach (var response in responses) ResponseRequested?.Invoke(response);
    }

    /// <summary>
    /// 生成当前屏幕的快照：活动缓冲区的单元格、滚动历史、光标以及影响输入编码的各项模式。
    /// </summary>
    /// <remarks>
    /// 每一行单元格数组都被复制；<see cref="TerminalCell"/> 本身不可变，因此快照与之后的屏幕变化完全无关，可以跨线程使用。
    /// </remarks>
    /// <returns>屏幕在持锁瞬间的状态。</returns>
    public TerminalSnapshot Snapshot()
    {
        lock (_gate)
            return new(_columns, _rows, _column, _row, _cursorVisible,
                _cells.Select(line => line.ToArray()).ToArray(), _history.ToArray(), _alternate,
                _bracketedPaste, _applicationCursor, _mouseTracking, _sgrMouse, _utf8Mouse);
    }

    /// <summary>
    /// 把要写给子程序的输入编码为字节：通常按 UTF-8 编码；
    /// 已协商的传统鼠标报告完整包则按 Latin-1 编码为 6 个原始字节。
    /// </summary>
    /// <remarks>
    /// 传统（X10 兼容）鼠标报告 <c>CSI M Cb Cx Cy</c> 的按键与坐标字节为“值 + 32”，可能超过 127；
    /// 若按 UTF-8 编码会变成两个字节，破坏协议。只有同时满足以下条件时才按原始字节发送：
    /// 鼠标跟踪模式为 1000/1002/1003，未启用 SGR（1006）与 UTF-8（1005）编码，
    /// 文本恰为 6 个字符且以 <c>ESC [ M</c> 开头，按键字节在 32–127、两个坐标字节在 33–255。
    /// </remarks>
    /// <param name="text">要发送的输入文本（按键序列、粘贴内容或鼠标报告）。</param>
    /// <returns>可直接写入伪控制台输入管道的字节。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/>。</exception>
    public byte[] EncodeInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            var legacyPacket = _mouseTracking is 1000 or 1002 or 1003 && !_sgrMouse && !_utf8Mouse &&
                text.Length == 6 && text.StartsWith("\u001b[M", StringComparison.Ordinal) &&
                text[3] is >= (char)32 and <= (char)127 && text[4] is >= (char)33 and <= (char)255 && text[5] is >= (char)33 and <= (char)255;
            return (legacyPacket ? Encoding.Latin1 : Encoding.UTF8).GetBytes(text);
        }
    }

    /// <summary>
    /// 返回活动缓冲区的纯文本：每行单元格文本拼接后去掉行尾空白，行间以 <c>\n</c> 连接。
    /// </summary>
    /// <returns>屏幕可见部分的文本，不含滚动历史；行数始终等于屏幕行数。</returns>
    public string GetText()
    {
        lock (_gate) return string.Join('\n', _cells.Select(LineText));
    }

    /// <summary>
    /// 拼接一行单元格的文本并去掉行尾空白。
    /// </summary>
    /// <param name="line">一行单元格；宽字符续位单元格的文本为空，不会产生多余字符。</param>
    /// <returns>该行文本。</returns>
    private static string LineText(TerminalCell[] line) => string.Concat(line.Select(cell => cell.Text)).TrimEnd();

    /// <summary>
    /// 处理一个可打印的 UTF-16 字符：把代理对拼成完整的 Unicode 标量后交给 <see cref="WriteRune"/>。
    /// </summary>
    /// <remarks>
    /// 高代理项先暂存，等待下一个字符；后面不是低代理项时，暂存的高代理项写为 U+FFFD，
    /// 当前字符照常处理。孤立的低代理项同样写为 U+FFFD。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="value">非控制字符。</param>
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

    /// <summary>
    /// 在光标处写入一个 Unicode 标量，处理字符集映射、组合字符、宽字符、自动换行与插入模式。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>当前字符集（SO 选中 G1，否则 G0）为 DEC 线条绘制时，0x60–0x7E 映射为对应的制表符和符号。</description></item>
    /// <item><description>宽度为 0 的字符（组合符、ZWJ、变体选择符、肤色修饰符）、紧跟在 ZWJ 之后的字符，以及与前一个区域指示符
    /// 配成国旗的区域指示符，都追加到前一个字形上（光标前一格；延迟换行时为光标所在格；跳过续位单元格），没有前一个字形时丢弃。</description></item>
    /// <item><description>追加 U+FE0F（emoji 呈现）、U+20E3（键帽）或组成国旗会把窄字形扩为两格；字形位于最后一列且允许自动换行时，
    /// 先把它移到下一行行首再加宽。</description></item>
    /// <item><description>普通字符写入前：延迟换行或宽字符落在最后一列时，自动换行开启则先换行；
    /// 仍放不下的宽字符（屏幕只有 1 列，或自动换行关闭时位于最后一列）写为 U+FFFD。</description></item>
    /// <item><description>写入会先清除被覆盖宽字符的另一半；写完后光标右移字符宽度，越过最后一列时停在最后一列并设置延迟换行。</description></item>
    /// </list>
    /// 调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="rune">要写入的字符。</param>
    private void WriteRune(Rune rune)
    {
        if ((_useG1 ? _g1LineDrawing : _g0LineDrawing) && rune.Value is >= 0x60 and <= 0x7e)
            rune = new Rune("◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"[rune.Value - 0x60]);
        var width = DisplayWidth(rune);
        var previous = _wrapPending ? _column : _column - 1;
        if (previous >= 0 && _cells[_row][previous].Width == 0) previous--;
        var previousText = previous >= 0 ? _cells[_row][previous].Text : "";
        var regionalPair = rune.Value is >= 0x1f1e6 and <= 0x1f1ff && previousText.Length == 2 &&
            Rune.GetRuneAt(previousText, 0).Value is >= 0x1f1e6 and <= 0x1f1ff;
        if (width == 0 || previousText.EndsWith('\u200d') || regionalPair)
        {
            if (previous >= 0)
            {
                var cell = _cells[_row][previous];
                // 单个字形簇的长度设上限：恶意输出（一个字母后跟上百万个组合符或 ZWJ）否则会让单元格无限增长、
                // 每次拼接都复制整个字符串，在屏幕锁内占满一个核心，并让屏幕快照超过协议上限。
                if (cell.Text.Length >= MaximumClusterLength) return;
                var widePresentation = rune.Value is 0xfe0f or 0x20e3 || regionalPair;
                if (widePresentation && cell.Width == 1 && previous == _columns - 1 && _columns > 1 && _autoWrap)
                {
                    ClearGlyph(_row, previous);
                    _column = 0;
                    Index();
                    previous = 0;
                    ClearGlyph(_row, previous);
                }
                var newWidth = widePresentation && previous + 1 < _columns ? 2 : cell.Width;
                if (newWidth > cell.Width)
                {
                    ClearGlyph(_row, previous + 1);
                    _cells[_row][previous + 1] = Blank() with { Text = "", Width = 0 };
                    _column = Math.Min(_columns - 1, previous + newWidth);
                    _wrapPending = previous + newWidth >= _columns;
                }
                _cells[_row][previous] = cell with { Text = cell.Text + rune, Width = newWidth };
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
        if (_insertMode) InsertCharacters(width);
        ClearGlyph(_row, _column);
        if (width == 2) ClearGlyph(_row, _column + 1);
        _cells[_row][_column] = new(rune.ToString(), _reverse ? _background : _foreground,
            _reverse ? _foreground : _background, _bold, width, _faint, _italic, _underline, _blink, _hidden, _strikethrough);
        if (width == 2) _cells[_row][_column + 1] = new("", _foreground, _background, _bold, 0);
        _column += width;
        if (_column >= _columns) { _column = _columns - 1; _wrapPending = true; }
    }

    /// <summary>
    /// 估算字符的显示宽度（单元格数）。
    /// </summary>
    /// <remarks>
    /// 这是简化的 wcwidth：非间距组合符、包围组合符、格式字符（ZWJ 等）与 emoji 肤色修饰符（U+1F3FB–U+1F3FF）为 0；
    /// 韩文字母、东亚宽字符与全角字符、韩文音节、CJK 兼容表意文字、区域指示符、U+1F300–U+1FAFF 的 emoji 区块
    /// 以及第 2、3 平面的表意文字为 2；其余为 1。
    /// </remarks>
    /// <param name="rune">字符。</param>
    /// <returns>0、1 或 2。</returns>
    private static int DisplayWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format || rune.Value is >= 0x1f3fb and <= 0x1f3ff) return 0;
        var c = rune.Value;
        if (c is >= 0x1100 and <= 0x115f or 0x2329 or 0x232a
            or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6 or >= 0x1f1e6 and <= 0x1f1ff or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd) return 2;
        return 1;
    }

    /// <summary>
    /// 把一个单元格清为空白，并一同清除它所属宽字符的另一半，避免留下半个宽字符。
    /// </summary>
    /// <remarks>
    /// 目标是续位单元格时清除前一列的首格；目标是宽字符首格时清除后一列的续位单元格。列越界时什么也不做。
    /// 调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="row">行（从 0 开始，必须在屏幕范围内）。</param>
    /// <param name="column">列（从 0 开始）。</param>
    private void ClearGlyph(int row, int column)
    {
        if (column < 0 || column >= _columns) return;
        if (_cells[row][column].Width == 0 && column > 0) _cells[row][column - 1] = Blank();
        if (_cells[row][column].Width == 2 && column + 1 < _columns) _cells[row][column + 1] = Blank();
        _cells[row][column] = Blank();
    }

    /// <summary>
    /// 创建一个空白单元格：空格、当前前景色与背景色、无文字样式。
    /// </summary>
    /// <remarks>
    /// 使用当前（而非默认）颜色，使擦除、插入、滚动产生的空白区域带上当前背景色（背景色擦除，BCE）；
    /// 不考虑反显状态。
    /// </remarks>
    /// <returns>新的空白单元格。</returns>
    private TerminalCell Blank() => new(" ", _foreground, _background, false);

    /// <summary>
    /// 创建填满 <see cref="Blank"/> 的单元格数组。
    /// </summary>
    /// <param name="columns">每行的列数。</param>
    /// <param name="rows">行数。</param>
    /// <returns>按 <c>[行][列]</c> 索引的新数组，每行都是独立的数组实例。</returns>
    private TerminalCell[][] CreateCells(int columns, int rows) =>
        Enumerable.Range(0, rows).Select(_ => Enumerable.Range(0, columns).Select(_ => Blank()).ToArray()).ToArray();

    /// <summary>
    /// 校验终端尺寸是否在支持范围内。<see cref="ConPtySession"/> 在创建伪控制台与 resize 前也调用它。
    /// </summary>
    /// <param name="columns">列数，允许 1–500。</param>
    /// <param name="rows">行数，允许 1–300。</param>
    /// <exception cref="ArgumentOutOfRangeException">列数或行数超出范围。</exception>
    internal static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be between 1 and 500.");
        if (rows is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(rows), "Rows must be between 1 and 300.");
    }
}
