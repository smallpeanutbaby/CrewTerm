namespace CrewTerm.Terminal.Tests;

public sealed class VtBehaviorTests
{
    private static string Line(TerminalScreen screen, int row) => TerminalScreenTests.Line(screen, row);

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

    [Fact]
    public void ScreenEraseAndRelativeMovementRespectBounds()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("abc\r\ndef\u001b[2J\u001b[HX\u001b[2BY\u001b[999CZ");
        Assert.Equal("X", Line(screen, 0));
        Assert.Equal("", Line(screen, 1));
        Assert.Equal(" Y       Z", Line(screen, 2));
    }

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

    [Fact]
    public void ReplacingHalfOfWideCharacterDoesNotLeaveGhostGlyph()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("中A\u001b[2GX");
        Assert.Equal(" XA", Line(screen, 0));
        Assert.Equal(1, screen.Snapshot().Cells[0][0].Width);
    }

    [Fact]
    public void OscAndDcsMetadataDoNotLeakIntoVisibleScreen()
    {
        var screen = new TerminalScreen(20, 2);
        screen.Feed("a\u001b]0;secret title\a");
        screen.Feed("b\u001bPignored payload\u001b");
        screen.Feed("\\c\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\");
        Assert.Equal("abclink", Line(screen, 0));
    }

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
