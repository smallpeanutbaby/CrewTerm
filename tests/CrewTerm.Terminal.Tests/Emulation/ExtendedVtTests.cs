using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Tests.Emulation;

/// <summary>
/// 覆盖 <see cref="TerminalScreen"/> 对较少见 VT/xterm 序列的解析：DEC 光标保存、SGR 扩展写法、原点模式、插入/换行模式、
/// DEC 线框字符集、整屏重置、多码点 Emoji、私有前缀与中间字节序列等。
/// 均为纯内存单元测试：直接向 <see cref="TerminalScreen.Feed"/> 输入字节文本，再检查屏幕快照、文本或应答。
/// </summary>
public sealed class ExtendedVtTests
{
    /// <summary>
    /// 验证：<c>ESC 7</c>（DECSC）在保存光标位置的同时保存红色前景、斜体与下划线属性；之后换成绿色并关闭斜体和下划线，
    /// <c>ESC 8</c>（DECRC）回到原位置写入的字符重新带上保存时的红色、斜体与下划线。
    /// </summary>
    [Fact]
    public void DecSavedCursorRestoresItsTextRendition()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("\u001b[31;3;4m\u001b7A\u001b[32;23;24mB\u001b8C");
        var cell = screen.Snapshot().Cells[0][0];
        Assert.Equal("C", cell.Text);
        Assert.Equal(0xcd3131, cell.Foreground);
        Assert.True(cell.Italic && cell.Underline);
    }

    /// <summary>
    /// 验证：4 列宽的屏幕上，<c>✈</c> 先以单宽字符写在最右一列；随后单独到达的 U+FE0F 变体选择符把它变成双宽 Emoji，
    /// 整个字形被移到下一行行首（宽度 2），原位置清为空格，后续的 <c>!</c> 紧随其后写在第 3 列。
    /// </summary>
    [Fact]
    public void EmojiVariationSelectorAtRightMarginMovesWholeGlyphToNextLine()
    {
        var screen = new TerminalScreen(4, 3);
        screen.Feed("abc✈");
        screen.Feed("\ufe0f!");
        var state = screen.Snapshot();
        Assert.Equal(" ", state.Cells[0][3].Text);
        Assert.Equal("✈\ufe0f", state.Cells[1][0].Text);
        Assert.Equal(2, state.Cells[1][0].Width);
        Assert.Equal("!", state.Cells[1][2].Text);
    }
    /// <summary>
    /// 验证：被拆成两次输入的 SGR <c>1;2;3;4;5;8;9</c> 能完整生效（粗体、暗淡、斜体、下划线、闪烁、隐藏、删除线全部开启），
    /// 而 SGR <c>22;23;24;25;28;29</c> 能把这些样式逐一关闭。
    /// </summary>
    [Fact]
    public void CommonTextStylesSurviveSplitInputAndResetIndependently()
    {
        var screen = new TerminalScreen(20, 3);
        screen.Feed("\u001b[1;2;3;4;5;");
        screen.Feed("8;9mA\u001b[22;23;24;25;28;29mB");
        var cells = screen.Snapshot().Cells[0];
        Assert.True(cells[0].Bold && cells[0].Faint && cells[0].Italic && cells[0].Underline && cells[0].Blink && cells[0].Hidden && cells[0].Strikethrough);
        Assert.False(cells[1].Bold || cells[1].Faint || cells[1].Italic || cells[1].Underline || cells[1].Blink || cells[1].Hidden || cells[1].Strikethrough);
    }

    /// <summary>
    /// 验证：冒号分隔的子参数被当作一个整体解析——<c>38:2::10:20:30</c> 设置真彩前景 <c>0x0a141e</c>，
    /// <c>48:5:17</c> 设置 256 色背景 <c>0x00005f</c>，<c>4:3</c>（波浪下划线）只开启下划线而不会把 3 误当作斜体，
    /// <c>4:0</c> 关闭下划线。
    /// </summary>
    [Fact]
    public void ColonTrueColorAndUnderlineStylesDoNotBecomeUnrelatedSgrCommands()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("\u001b[38:2::10:20:30;48:5:17;4:3mA\u001b[4:0mB");
        var cells = screen.Snapshot().Cells[0];
        Assert.Equal(0x0a141e, cells[0].Foreground);
        Assert.Equal(0x00005f, cells[0].Background);
        Assert.True(cells[0].Underline);
        Assert.False(cells[0].Italic);
        Assert.False(cells[1].Underline);
    }

    /// <summary>
    /// 验证：设置滚动区域为第 2～5 行并开启原点模式（<c>CSI ?6h</c>）后，光标定位相对滚动区域顶部计算，
    /// <c>CSI 6n</c> 应答相对位置 <c>CSI 2;4R</c>，大幅下移会被限制在区域底边；关闭原点模式后定位恢复为相对整屏。
    /// </summary>
    [Fact]
    public void OriginModeAddressesInsideMarginsAndReportsRelativePosition()
    {
        var screen = new TerminalScreen(10, 6);
        var replies = new List<string>();
        screen.ResponseRequested += replies.Add;
        screen.Feed("\u001b[2;5r\u001b[?6h\u001b[2;3HX\u001b[6n\u001b[99B");
        Assert.Equal("X", screen.Snapshot().Cells[2][2].Text);
        Assert.Equal(["\u001b[2;4R"], replies);
        Assert.Equal(4, screen.Snapshot().CursorRow);
        screen.Feed("\u001b[?6l\u001b[1;1HY");
        Assert.Equal("Y", screen.Snapshot().Cells[0][0].Text);
    }

    /// <summary>
    /// 验证：插入模式（<c>CSI 4h</c>）下写入的字符把原有文本右移，<c>CSI 4l</c> 后恢复覆盖写入；
    /// 新行模式（LNM，<c>CSI 20h</c>）下换行符同时回到行首，<c>CSI 20l</c> 后换行符只下移一行、列位置不变。
    /// </summary>
    [Fact]
    public void InsertAndNewlineModesCanBeEnabledAndReset()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("abcd\r\u001b[4hXY\u001b[4lZ\u001b[20h\nN\u001b[20l\nQ");
        Assert.StartsWith("XYZbcd", screen.GetText().Split('\n')[0]);
        Assert.Equal("N", screen.Snapshot().Cells[1][0].Text);
        Assert.Equal("Q", screen.Snapshot().Cells[2][1].Text);
    }

    /// <summary>
    /// 验证：把 G0 指定为 DEC 特殊图形字符集（<c>ESC ( 0</c>）后 <c>lqk</c> 显示为 <c>┌─┐</c>，<c>ESC ( B</c> 恢复 ASCII；
    /// 把 G1 指定为线框字符集后，SO（Shift Out）切换到 G1 输出 <c>│─</c>，SI（Shift In）切回 G0 输出普通 <c>q</c>。
    /// </summary>
    [Fact]
    public void DecLineDrawingCharsetAndShiftInOutProduceBoxCharacters()
    {
        var screen = new TerminalScreen(20, 3);
        screen.Feed("\u001b(0lqk\u001b(Babc\u001b)0\u000exq\u000fq");
        Assert.StartsWith("┌─┐abc│─q", screen.GetText());
    }

    /// <summary>
    /// 验证：在产生滚动历史、开启鼠标跟踪/SGR 鼠标/括号粘贴/应用光标键/原点模式、设置红色斜体并切换到线框字符集之后，
    /// <c>ESC c</c>（RIS 完全重置）清空历史、关闭全部模式，随后写入的 <c>q</c> 以 ASCII 字符和默认前景色、非斜体样式显示在左上角。
    /// </summary>
    [Fact]
    public void ResetRestoresDefaultScreenColorsModesAndHistory()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("a\r\nb\r\nc\u001b[?1003;1006;2004;1;6h\u001b[31;3m\u001b(0\u001bcq");
        var state = screen.Snapshot();
        Assert.Empty(state.History);
        Assert.Equal(0, state.MouseTracking);
        Assert.False(state.SgrMouse || state.BracketedPaste || state.ApplicationCursorKeys || state.AlternateScreen);
        Assert.Equal("q", state.Cells[0][0].Text);
        Assert.Equal(TerminalScreen.DefaultForeground, state.Cells[0][0].Foreground);
        Assert.False(state.Cells[0][0].Italic);
    }

    /// <summary>
    /// 验证：ZWJ 组合、肤色修饰、国旗区域指示符和 U+FE0F 变体选择符这几类常见 Emoji 序列，即使按单个 UTF-16 代码单元
    /// 逐个输入，也合并为一个宽度为 2 的字素单元格；其后的 <c>!</c> 紧接着写入，光标停在其后一列。
    /// </summary>
    /// <param name="text">输入文本：一个 Emoji 序列后跟 <c>!</c>。</param>
    /// <param name="glyph">期望首个单元格保存的完整字素。</param>
    /// <param name="width">期望该字素占用的列数。</param>
    [Theory]
    [InlineData("👩\u200d💻!", "👩\u200d💻", 2)]
    [InlineData("👍🏽!", "👍🏽", 2)]
    [InlineData("🇨🇳!", "🇨🇳", 2)]
    [InlineData("✈\ufe0f!", "✈\ufe0f", 2)]
    public void CommonEmojiSequencesRemainOneWideGraphemeAcrossChunks(string text, string glyph, int width)
    {
        var screen = new TerminalScreen(20, 3);
        foreach (var c in text) screen.Feed(c.ToString());
        var state = screen.Snapshot();
        Assert.Equal(glyph, state.Cells[0][0].Text);
        Assert.Equal(width, state.Cells[0][0].Width);
        Assert.Equal("!", state.Cells[0][width].Text);
        Assert.Equal(width + 1, state.CursorColumn);
    }

    /// <summary>
    /// 验证：带 <c>&gt;</c>、<c>&lt;</c>、<c>=</c>、<c>?</c> 私有前缀的序列（kitty 键盘协议、XTMODKEYS、XTRESTORE、XTSAVE、DA3）
    /// 不会被当作同一结尾字节的标准命令执行：光标不被“恢复”到 <c>CSI s</c> 保存的位置，不开启粗体，不改变滚动区域，也不产生应答；
    /// 而 kitty 键盘标志查询 <c>CSI ?u</c> 与次设备属性查询 <c>CSI &gt;c</c> 会得到预期应答。
    /// </summary>
    [Fact]
    public void PrivatePrefixSequencesAreNotExecutedAsStandardCommands()
    {
        var screen = new TerminalScreen(10, 4);
        var responses = new List<string>();
        screen.ResponseRequested += responses.Add;
        screen.Feed("\u001b[2;3H\u001b[s\u001b[4;5H");
        // kitty 键盘协议 push/pop/set、XTMODKEYS、XTRESTORE、XTSAVE、DA3 都不能改变光标、样式或滚动区域。
        screen.Feed("\u001b[>1u\u001b[<u\u001b[=1;1u\u001b[>4;1m\u001b[?2;3r\u001b[?1s\u001b[=c");
        screen.Feed("A");
        var state = screen.Snapshot();
        Assert.Equal("A", state.Cells[3][4].Text);
        Assert.False(state.Cells[3][4].Bold);
        Assert.Empty(responses);
        screen.Feed("\u001b[?u\u001b[>c");
        Assert.Equal(["\u001b[?0u", "\u001b[>0;10;1c"], responses);
    }

    /// <summary>
    /// 验证：带空格中间字节的 CSI 序列（<c>CSI 3 SP @</c> 左滚、<c>CSI 2 SP q</c> 光标样式）被忽略，
    /// 不会被当作插入空白字符等标准命令执行，随后写入的 <c>X</c> 只覆盖行首字符，得到 <c>Xbc</c>。
    /// </summary>
    [Fact]
    public void SequencesWithIntermediateBytesAreIgnored()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abc\u001b[1G\u001b[3 @\u001b[2 qX");
        Assert.Equal("Xbc", screen.GetText().Split('\n')[0]);
    }

    /// <summary>
    /// 验证：CSI 序列中途出现的回车符会被立即执行（光标回到行首），但不会中断该序列；
    /// 序列随后以 <c>2C</c> 完成、光标右移 2 列，写入的 <c>X</c> 得到 <c>abX</c>。
    /// </summary>
    [Fact]
    public void ControlCharactersInsideCsiExecuteWithoutAbortingTheSequence()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("abc\u001b[\r2CX");
        Assert.Equal("abX", screen.GetText().Split('\n')[0]);
    }

    /// <summary>
    /// 验证：带中间字节的 ESC 序列 <c>ESC % G</c>（选择 UTF-8）与 <c>ESC SP F</c>（7 位控制字符）被完整消费，
    /// 结尾字节 <c>G</c>、<c>F</c> 不会被当作文本打印，屏幕上只有 <c>AB</c>。
    /// </summary>
    [Fact]
    public void TwoByteEscapeSequencesDoNotPrintTheirFinalByte()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("\u001b%GA\u001b FB");
        Assert.Equal("AB", screen.GetText().Split('\n')[0]);
    }

    /// <summary>
    /// 验证：被截断的扩展颜色 <c>CSI 38;5 m</c>（缺少色号）不会把其中的 <c>5</c> 当作“闪烁”执行；
    /// 完整的 <c>CSI 38;5;1;4 m</c> 设置调色板 1 号前景色后，随后的 <c>4</c> 仍作为下划线生效。
    /// </summary>
    [Fact]
    public void TruncatedExtendedColorDoesNotLeakItsSubParameters()
    {
        var colon = new TerminalScreen(10, 2);
        // 冒号形式缺少分量时整组丢弃，后面以分号分隔的 1（加粗）与 4（下划线）照常生效。
        colon.Feed("\u001b[38:2:1:2;1;4mB");
        Assert.True(colon.Snapshot().Cells[0][0].Bold && colon.Snapshot().Cells[0][0].Underline);
        var underlineColor = new TerminalScreen(10, 2);
        // 58（下划线颜色）消费其子参数后忽略，不会把 2 当作暗淡、把 0 当作重置。
        underlineColor.Feed("\u001b[1m\u001b[58;2;255;0;0mU");
        Assert.True(underlineColor.Snapshot().Cells[0][0].Bold);
        Assert.False(underlineColor.Snapshot().Cells[0][0].Faint);
        var screen = new TerminalScreen(10, 2);
        screen.Feed("\u001b[38;5mA\u001b[0m\u001b[38;5;1;4mB");
        var cells = screen.Snapshot().Cells[0];
        Assert.False(cells[0].Blink);
        Assert.Equal(0xCD3131, cells[1].Foreground);
        Assert.True(cells[1].Underline);
        Assert.False(cells[1].Blink);
    }

    /// <summary>
    /// 验证：一个字母后跟上万个组合符时，单元格中的字形簇被截断在上限以内（防止无限增长与平方级复制），后续字符照常显示。
    /// </summary>
    [Fact]
    public void CombiningFloodIsBoundedPerCell()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("a" + new string('́', 20000) + "b");
        var cells = screen.Snapshot().Cells[0];
        Assert.Equal('a', cells[0].Text[0]);
        Assert.True(cells[0].Text.Length <= 64);
        Assert.Equal("b", cells[1].Text);
    }

    /// <summary>
    /// 验证：超过长度上限的 CSI 序列被整体吞掉直到结束字节，既不执行，也不会把其中的参数字节打印到屏幕上。
    /// </summary>
    [Fact]
    public void OverlongCsiIsSwallowedWithoutPrintingItsParameters()
    {
        var screen = new TerminalScreen(20, 2);
        screen.Feed("\u001b[" + new string('1', 300) + "mX");
        Assert.Equal("X", screen.GetText().Split('\n')[0]);
        Assert.False(screen.Snapshot().Cells[0][0].Bold);
    }
}

