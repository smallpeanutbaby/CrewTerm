namespace CrewTerm.Terminal.Tests;

public sealed class ResizeAndQueryTests
{
    [Fact]
    public void ShrinkingKeepsCursorContentVisibleAndGrowingPreservesIt()
    {
        var screen = new TerminalScreen(12, 4);
        screen.Feed("first\r\nsecond\r\nthird\r\nfourth");
        screen.Resize(10, 2);
        Assert.Equal("third", TerminalScreenTests.Line(screen, 0));
        Assert.Equal("fourth", TerminalScreenTests.Line(screen, 1));
        Assert.Equal(1, screen.Snapshot().CursorRow);
        screen.Resize(15, 5);
        Assert.Equal("fourth", TerminalScreenTests.Line(screen, 1));
        Assert.Equal(15, screen.Snapshot().Cells[0].Length);
    }

    [Fact]
    public void ResizingAlternateScreenAlsoResizesSavedPrimary()
    {
        var screen = new TerminalScreen(12, 4);
        screen.Feed("original\u001b[?1049hother");
        screen.Resize(6, 2);
        screen.Feed("\u001b[?1049l");
        var snapshot = screen.Snapshot();
        Assert.Equal(2, snapshot.Cells.Length);
        Assert.Equal(6, snapshot.Cells[0].Length);
        Assert.Equal("origin", TerminalScreenTests.Line(screen, 0));
    }

    [Fact]
    public void TerminalQueriesProduceRepliesWithoutVisibleText()
    {
        var screen = new TerminalScreen(12, 4);
        var replies = new List<string>();
        screen.ResponseRequested += replies.Add;
        screen.Feed("ab\u001b[6n\u001b[5n\u001b[c\u001b[?u");
        Assert.Equal(new[] { "\u001b[1;3R", "\u001b[0n", "\u001b[?1;2c", "\u001b[?0u" }, replies);
        Assert.Equal("ab", TerminalScreenTests.Line(screen, 0));
    }

    [Fact]
    public void QueryReplyCallbacksDoNotHoldTheScreenLock()
    {
        var screen = new TerminalScreen(12, 4);
        using var snapshotTaken = new ManualResetEventSlim();
        var worker = new Thread(() => { screen.Snapshot(); snapshotTaken.Set(); }) { IsBackground = true };
        var snapshotWasAvailableInsideCallback = false;
        screen.ResponseRequested += _ =>
        {
            worker.Start();
            snapshotWasAvailableInsideCallback = snapshotTaken.Wait(TimeSpan.FromSeconds(1));
        };
        screen.Feed("\u001b[6n");
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
        Assert.True(snapshotWasAvailableInsideCallback, "Reply callbacks must not hold screen state while native input/resize takes its own lock.");
    }
}
