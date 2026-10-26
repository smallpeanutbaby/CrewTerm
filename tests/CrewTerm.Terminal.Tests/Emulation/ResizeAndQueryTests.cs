using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Tests.Emulation;

/// <summary>
/// 覆盖 <see cref="TerminalScreen"/> 的尺寸调整（主屏与备用屏）以及终端查询序列的应答，
/// 包括应答回调与屏幕锁之间的关系。均为纯内存单元测试，只有锁测试额外使用一个后台线程。
/// </summary>
public sealed class ResizeAndQueryTests
{
    /// <summary>
    /// 验证：4 行内容的屏幕缩小到 10×2 后，保留光标所在的最后两行（<c>third</c>、<c>fourth</c>）可见且光标位于第 2 行；
    /// 再放大到 15×5 时，<c>fourth</c> 仍在原来的第 2 行，每行宽度扩展为 15 列。
    /// </summary>
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

    /// <summary>
    /// 验证：处于备用屏（<c>CSI ?1049h</c>）时调整为 6×2，退出备用屏后恢复的主屏同样是 2 行 6 列，
    /// 原有文本 <c>original</c> 被截断为 <c>origin</c>。
    /// </summary>
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

    /// <summary>
    /// 验证：光标位置查询（<c>CSI 6n</c>）、设备状态查询（<c>CSI 5n</c>）、主设备属性查询（<c>CSI c</c>）和
    /// kitty 键盘标志查询（<c>CSI ?u</c>）按顺序通过 <see cref="TerminalScreen.ResponseRequested"/> 产生对应应答，
    /// 屏幕上只留下普通文本 <c>ab</c>，查询本身不显示。
    /// </summary>
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

    /// <summary>
    /// 验证：<see cref="TerminalScreen.ResponseRequested"/> 回调执行时不持有屏幕锁——在回调内启动的另一个线程
    /// 能在 1 秒内取得 <see cref="TerminalScreen.Snapshot"/>，该线程也能在 3 秒内结束，避免与输入/尺寸调整互相死锁。
    /// </summary>
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
