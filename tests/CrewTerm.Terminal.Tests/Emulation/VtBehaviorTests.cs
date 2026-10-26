using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Tests.Emulation;

/// <summary>
/// 覆盖 <see cref="TerminalScreen"/> 对常用 VT 行为的实现：延迟自动换行、有界滚动历史、备用屏、SGR 颜色、擦除与相对移动、
/// 滚动区域、字符插入/删除/擦除、宽字符与组合字符、OSC/DCS 元数据过滤、输入模式记录以及光标保存/恢复与反向换行。
/// 均为纯内存单元测试：直接输入文本后检查屏幕快照与逐行文本。
/// </summary>
public sealed class VtBehaviorTests
{
    /// <summary>
    /// 取屏幕指定行去除行尾空白后的文本；转发到 <see cref="TerminalScreenTests.Line"/>。
    /// </summary>
    /// <param name="screen">要读取的屏幕。</param>
    /// <param name="row">行索引（从 0 开始）。</param>
    /// <returns>该行去除行尾空白后的文本。</returns>
    private static string Line(TerminalScreen screen, int row) => TerminalScreenTests.Line(screen, row);

    /// <summary>
    /// 验证：4 列宽的屏幕写满一行后光标仍停留在第一行（延迟换行），直到写入下一个可打印字符 <c>e</c> 才换到第二行。
    /// </summary>
    [Fact]
    public void AutoWrapWaitsForNextPrintableCharacter()
    {
        var screen = new TerminalScreen(4, 2);
        screen.Feed("abcd");
        Assert.Equal(0, screen.Snapshot().CursorRow);
        screen.Feed("e");
        Assert.Equal("abcd", Line(screen, 0));
        Assert.Equal("e", Line(screen, 1));
    }

    /// <summary>
    /// 验证：在 2 行高的屏幕上输出 2102 行后，滚动历史被限制为 2000 行且保留最新的内容——历史最后一行为 <c>2100</c>，
    /// 可见区域第一行为 <c>2101</c>。
    /// </summary>
    [Fact]
    public void ScrollbackIsBoundedAndKeepsNewestLines()
    {
        var screen = new TerminalScreen(10, 2);
        for (var i = 0; i < 2102; i++) screen.Feed($"{i}\r\n");
        var snapshot = screen.Snapshot();
        Assert.Equal(2000, snapshot.History.Length);
        Assert.Equal("2100", snapshot.History[^1]);
        Assert.Equal("2101", Line(screen, 0));
    }

    /// <summary>
    /// 验证：<c>CSI ?1049h</c> 进入备用屏后快照标记为备用屏并显示新内容；<c>CSI ?1049l</c> 退出后恢复主屏原有单元格与光标位置，
    /// 随后写入的 <c>!</c> 紧接在 <c>prompt</c> 之后。
    /// </summary>
    [Fact]
    public void AlternateScreenRestoresOriginalCellsAndCursor()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("prompt\u001b[?1049hother");
        Assert.True(screen.Snapshot().AlternateScreen);
        Assert.Equal("other", Line(screen, 0));
        screen.Feed("\u001b[?1049l!");
        Assert.False(screen.Snapshot().AlternateScreen);
        Assert.Equal("prompt!", Line(screen, 0));
    }

    /// <summary>
    /// 验证：SGR <c>1;31</c> 产生粗体红色（<c>0xCD3131</c>），<c>38;5;196</c> 与 <c>48;2;1;2;3</c> 分别设置 256 色前景
    /// <c>0xFF0000</c> 和真彩背景 <c>0x010203</c>，<c>0</c> 把粗体与前景色恢复为默认值。
    /// </summary>
    [Fact]
    public void SgrRetainsAnsiIndexedAndTrueColorAttributes()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("\u001b[1;31mA\u001b[38;5;196;48;2;1;2;3mB\u001b[0mC");
        var cells = screen.Snapshot().Cells[0];
        Assert.True(cells[0].Bold);
        Assert.Equal(0xCD3131, cells[0].Foreground);
        Assert.Equal(0xFF0000, cells[1].Foreground);
        Assert.Equal(0x010203, cells[1].Background);
        Assert.False(cells[2].Bold);
        Assert.Equal(TerminalScreen.DefaultForeground, cells[2].Foreground);
    }

    /// <summary>
    /// 验证：<c>CSI 2J</c> 清空整屏，<c>CSI H</c> 回到左上角；相对下移 2 行后写入 <c>Y</c>，再以超大步长 <c>CSI 999C</c> 右移时
    /// 光标被限制在最右一列写入 <c>Z</c>，第二行保持为空。
    /// </summary>
    [Fact]
    public void ScreenEraseAndRelativeMovementRespectBounds()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("abc\r\ndef\u001b[2J\u001b[HX\u001b[2BY\u001b[999CZ");
        Assert.Equal("X", Line(screen, 0));
        Assert.Equal("", Line(screen, 1));
        Assert.Equal(" Y       Z", Line(screen, 2));
    }

    /// <summary>
    /// 验证：滚动区域设为第 2～3 行后，在区域底行换行只滚动区域内部（<c>one</c> 移出、<c>two</c> 上移、新行写入 <c>new</c>），
    /// 区域外的首行 <c>header</c> 与末行 <c>footer</c> 保持不动。
    /// </summary>
    [Fact]
    public void ScrollRegionLeavesHeaderAndFooterUntouched()
    {
        var screen = new TerminalScreen(8, 4);
        screen.Feed("header\r\none\r\ntwo\r\nfooter\u001b[2;3r\u001b[3;1H\nnew");
        Assert.Equal("header", Line(screen, 0));
        Assert.Equal("two", Line(screen, 1));
        Assert.Equal("new", Line(screen, 2));
        Assert.Equal("footer", Line(screen, 3));
    }

    /// <summary>
    /// 验证：<c>CSI 2P</c> 删除光标处 2 个字符并左移后续文本，<c>CSI 2@</c> 在光标处插入 2 个空白并右移后续文本，
    /// <c>CSI 2X</c> 把光标处 2 个字符擦成空白而不移动其他文本，每一步的行内容都符合预期。
    /// </summary>
    [Fact]
    public void CharacterInsertDeleteAndEraseUpdateVisibleCells()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abcdef\u001b[3G\u001b[2PX");
        Assert.Equal("abXf", Line(screen, 0));
        screen.Feed("\u001b[2G\u001b[2@YZ");
        Assert.Equal("aYZbXf", Line(screen, 0));
        screen.Feed("\u001b[2X");
        Assert.Equal("aYZ  f", Line(screen, 0));
    }

    /// <summary>
    /// 验证：中文字符占 2 列（第二列为宽度 0 的占位单元格）；代理对被拆在两次输入中的 Emoji 仍合并为一个占 2 列的字符；
    /// 各字符落在正确的显示列，最终光标列索引为 6。
    /// </summary>
    [Fact]
    public void ChineseAndSplitEmojiUseDisplayColumns()
    {
        var screen = new TerminalScreen(12, 2);
        screen.Feed("中A\ud83d");
        screen.Feed("\ude00B");
        var snapshot = screen.Snapshot();
        Assert.Equal("中A😀B", Line(screen, 0));
        Assert.Equal(2, snapshot.Cells[0][0].Width);
        Assert.Equal(0, snapshot.Cells[0][1].Width);
        Assert.Equal("A", snapshot.Cells[0][2].Text);
        Assert.Equal("😀", snapshot.Cells[0][3].Text);
        Assert.Equal(6, snapshot.CursorColumn);
    }

    /// <summary>
    /// 验证：组合重音符 U+0301 附着在前一个基字符 <c>e</c> 所在的单元格中，不单独占列；后续的 <c>x</c> 位于第 2 列，光标列索引为 2。
    /// </summary>
    [Fact]
    public void CombiningMarksStayOnBaseCell()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("e\u0301x");
        var snapshot = screen.Snapshot();
        Assert.Equal("e\u0301", snapshot.Cells[0][0].Text);
        Assert.Equal("x", snapshot.Cells[0][1].Text);
        Assert.Equal(2, snapshot.CursorColumn);
    }

    /// <summary>
    /// 验证：在宽字符 <c>中</c> 的右半格写入 <c>X</c> 时，整个宽字符被清除——左半格变为宽度 1 的空格，
    /// 不会残留半个字形，行内容为 <c> XA</c>。
    /// </summary>
    [Fact]
    public void ReplacingHalfOfWideCharacterDoesNotLeaveGhostGlyph()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("中A\u001b[2GX");
        Assert.Equal(" XA", Line(screen, 0));
        Assert.Equal(1, screen.Snapshot().Cells[0][0].Width);
    }

    /// <summary>
    /// 验证：以 BEL 结尾的 OSC 0 窗口标题、终止符 ST 被拆在两次输入之间的 DCS 负载，以及以 ST 结尾的 OSC 8 超链接
    /// 都不会把元数据显示到屏幕上，可见文本只有 <c>abclink</c>。
    /// </summary>
    [Fact]
    public void OscAndDcsMetadataDoNotLeakIntoVisibleScreen()
    {
        var screen = new TerminalScreen(20, 2);
        screen.Feed("a\u001b]0;secret title\a");
        screen.Feed("b\u001bPignored payload\u001b");
        screen.Feed("\\c\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\");
        Assert.Equal("abclink", Line(screen, 0));
    }

    /// <summary>
    /// 验证：隐藏光标（<c>?25l</c>）、括号粘贴（<c>?2004h</c>）、应用光标键（<c>?1h</c>）、按钮事件（含拖动）鼠标跟踪（<c>?1002h</c>）
    /// 与 SGR 鼠标编码（<c>?1006h</c>）都被记录到屏幕快照中。
    /// </summary>
    [Fact]
    public void InputModesAndCursorVisibilityAreRecorded()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("\u001b[?25l\u001b[?2004h\u001b[?1h\u001b[?1002;1006h");
        var snapshot = screen.Snapshot();
        Assert.False(snapshot.CursorVisible);
        Assert.True(snapshot.BracketedPaste);
        Assert.True(snapshot.ApplicationCursorKeys);
        Assert.Equal(1002, snapshot.MouseTracking);
        Assert.True(snapshot.SgrMouse);
    }

    /// <summary>
    /// 验证：<c>ESC 7</c> 保存光标后换行输出，再用 <c>ESC 8</c> 恢复到第一行末尾写入 <c>X</c>（<c>firstX</c>）；
    /// 光标位于首行时 <c>ESC M</c>（反向换行）使屏幕整体下滚，新空行写入 <c>top</c>，原第一行下移到第二行。
    /// </summary>
    [Fact]
    public void SaveRestoreCursorAndReverseIndexWork()
    {
        var screen = new TerminalScreen(8, 3);
        screen.Feed("first\u001b7\r\nsecond\u001b8X");
        Assert.Equal("firstX", Line(screen, 0));
        screen.Feed("\u001b[H\u001bMtop");
        Assert.Equal("top", Line(screen, 0));
        Assert.Equal("firstX", Line(screen, 1));
    }
}
