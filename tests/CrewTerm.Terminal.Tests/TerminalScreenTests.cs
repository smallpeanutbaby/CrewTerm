namespace CrewTerm.Terminal.Tests;

public sealed class TerminalScreenTests
{
    [Fact]
    public void CursorAddressingOverwritesExistingText()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("hello\r\nworld\u001b[1;1HX");
        Assert.Equal("Xello", Line(screen, 0));
        Assert.Equal("world", Line(screen, 1));
        Assert.Equal(1, screen.Snapshot().CursorColumn);
    }

    [Fact]
    public void EscapeSequencesCanBeSplitAcrossReads()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abc\u001b[");
        screen.Feed("2D");
        screen.Feed("X");
        Assert.Equal("aXc", Line(screen, 0));
    }

    [Fact]
    public void EraseLineRemovesOnlyRequestedSection()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abcdef\u001b[4G\u001b[K");
        Assert.Equal("abc", Line(screen, 0));
    }

    [Fact]
    public void SnapshotsAreDetachedFromFutureWrites()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("before");
        var before = screen.Snapshot();
        screen.Feed("\rAFTER");
        Assert.Equal("b", before.Cells[0][0].Text);
        before.Cells[0][0] = before.Cells[0][0] with { Text = "changed" };
        Assert.Equal("A", screen.Snapshot().Cells[0][0].Text);
    }

    internal static string Line(TerminalScreen screen, int index) =>
        string.Concat(screen.Snapshot().Cells[index].Select(c => c.Text)).TrimEnd();
}
