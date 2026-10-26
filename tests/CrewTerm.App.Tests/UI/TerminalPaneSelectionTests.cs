using System.Drawing;
using CrewTerm.Terminal.Emulation;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="CrewTerm.App.UI.Controls.TerminalPane"/> 文本选择的单元测试：借助 <see cref="SelectionPaneFixture"/> 投递真实的视图鼠标事件，
/// 覆盖拖动选择与复制、宽字符与组合字符、冻结屏幕与本地历史滚动、切换会话时的清理，以及本地选择与子程序鼠标跟踪之间的优先级。
/// </summary>
public sealed class TerminalPaneSelectionTests
{
    /// <summary>
    /// 验证：终端为 10 列时产生的历史行在缩小到 5 列后，向上滚动 1 行并选择第一行，得到的是该行左侧的 5 个字符 <c>abcde</c>（从左侧截取显示，而不是重新换行）。
    /// </summary>
    [Fact]
    public void HistoryFromAWiderTerminalIsClippedFromTheLeftAfterResize()
    {
        using var fixture = new SelectionPaneFixture("");
        var screen = new TerminalScreen(10, 3);
        screen.Feed("abcdefghij\r\none\r\ntwo\r\nthree");
        screen.Resize(5, 3);
        fixture.Pane.Update(screen.Snapshot());
        fixture.Pane.ScrollHistory(1);
        fixture.Drag(new(0, 0), new(4, 0));
        Assert.Equal("abcde", fixture.Pane.SelectedText);
    }
    /// <summary>
    /// 验证：在真实 <see cref="Application"/> 中按下左键使面板捕获鼠标后切换会话，面板会释放鼠标捕获；随后在新会话屏幕（未启用跟踪）上拖动选择得到新内容 <c>vwx</c>。
    /// </summary>
    /// <param name="tracking">切换前的会话启用的鼠标跟踪模式；0 表示未启用。</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1002)]
    public void ChangingSessionReleasesTheRealApplicationMouseGrab(int tracking)
    {
        using var app = Application.Create();
        using var fixture = new SelectionPaneFixture("abcdef", tracking);
        fixture.Pane.App = app;
        fixture.Event(MouseFlags.LeftButtonPressed, new(2, 0));
        Assert.True(app.Mouse.IsGrabbed(fixture.Pane));
        fixture.Pane.SessionId = "other";
        Assert.False(app.Mouse.IsGrabbed(fixture.Pane));
        fixture.Pane.Update(Screen("uvwxyz"));
        fixture.Drag(new(1, 0), new(3, 0));
        Assert.Equal("vwx", fixture.Pane.SelectedText);
    }
    /// <summary>
    /// 验证：子程序启用 1002 跟踪时拖出面板视口再释放，拖动与释放事件的坐标被夹到面板最后一列（第 10 列）并照常转发，子程序能收到释放事件。
    /// </summary>
    [Fact]
    public void ApplicationDragReleaseOutsideTheViewportIsClampedAndDelivered()
    {
        using var fixture = new SelectionPaneFixture("abcdef", tracking: 1002);
        fixture.Event(MouseFlags.LeftButtonPressed, new(2, 1));
        fixture.Event(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, new(12, 1));
        fixture.Event(MouseFlags.LeftButtonReleased, new(12, 1));
        Assert.Equal(["\u001b[<0;3;2M", "\u001b[<32;10;2M", "\u001b[<0;10;2m"], fixture.Input);
    }

    /// <summary>
    /// 验证：本地选择时拖出视口右侧再释放，选择在面板边界处完成（选中 <c>cdefghij</c>）并复制一次。
    /// </summary>
    [Fact]
    public void LocalDragReleaseOutsideViewportCompletesSelectionAtBoundary()
    {
        using var fixture = new SelectionPaneFixture("abcdefghij");
        fixture.Event(MouseFlags.LeftButtonPressed, new(2, 0));
        fixture.Event(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, new(12, 0));
        fixture.Event(MouseFlags.LeftButtonReleased, new(12, 0));
        Assert.Equal("cdefghij", fixture.Pane.SelectedText);
        Assert.Equal(["cdefghij"], fixture.Copies);
    }
    /// <summary>
    /// 验证：切换会话后旧会话的选择被清除；在新会话的屏幕上重新拖动选择得到新内容 <c>VWX</c>，而不是旧的冻结屏幕内容。
    /// </summary>
    [Fact]
    public void ChangingSessionClearsFrozenSelectionAndHistory()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        fixture.Drag(new(1, 0), new(3, 0));
        fixture.Pane.SessionId = "another";
        fixture.Pane.Update(Screen("UVWXYZ"));
        Assert.False(fixture.Pane.HasSelection);
        fixture.Drag(new(1, 0), new(3, 0));
        Assert.Equal("VWX", fixture.Pane.SelectedText);
    }

    /// <summary>
    /// 验证：子程序启用鼠标跟踪时，Shift+右键仍打开本地上下文菜单（以屏幕坐标 (7,3) 请求），且不向子程序发送输入。
    /// </summary>
    [Fact]
    public void ShiftRightClickOpensLocalMenuWhileApplicationTracksMouse()
    {
        using var fixture = new SelectionPaneFixture("abcdef", tracking: 1000);
        var positions = new List<Point>();
        fixture.Pane.ContextMenuRequested = positions.Add;
        fixture.Event(MouseFlags.RightButtonPressed | MouseFlags.Shift, new(2, 1));
        Assert.Equal([new Point(7, 3)], positions);
        Assert.Empty(fixture.Input);
    }
    /// <summary>
    /// 验证：通过真实视图鼠标事件拖动选中 <c>bcde</c> 后自动复制一次，且不向子程序发送输入；再显式调用 <c>CopySelection</c> 会再复制一次。
    /// </summary>
    [Fact]
    public void DragSelectsTextAndCopiesOnceThroughRealViewEvents()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        fixture.Drag(new(1, 0), new(4, 0));
        Assert.True(fixture.Pane.HasSelection);
        Assert.Equal("bcde", fixture.Pane.SelectedText);
        Assert.Equal(["bcde"], fixture.Copies);
        Assert.Empty(fixture.Input);
        fixture.Pane.CopySelection();
        Assert.Equal(["bcde", "bcde"], fixture.Copies);
    }

    /// <summary>
    /// 验证：从第二行向上反向拖到第一行时，选中文本保留行间换行（<c>bc\nde</c>），并去掉行尾的空白列。
    /// </summary>
    [Fact]
    public void ReverseMultiLineSelectionPreservesNewlinesButTrimsBlankColumns()
    {
        using var fixture = new SelectionPaneFixture("abc\r\ndef");
        fixture.Drag(new(1, 1), new(1, 0));
        Assert.Equal("bc\nde", fixture.Pane.SelectedText);
    }

    /// <summary>
    /// 验证：选择从宽字符的续接单元格开始、并覆盖带组合符号的字符时，每个宽字符只复制一次，组合符号随其基字符一起复制。
    /// </summary>
    [Fact]
    public void WideCellContinuationAndCombiningCharactersAreCopiedOnlyOnce()
    {
        using var fixture = new SelectionPaneFixture("中文e\u0301z");
        fixture.Drag(new(1, 0), new(4, 0));
        Assert.Equal("中文e\u0301", fixture.Pane.SelectedText);
    }

    /// <summary>
    /// 验证：子程序启用 1003 跟踪时，Shift+拖动仍进行本地选择（<c>bcd</c>），不向子程序发送输入。
    /// </summary>
    [Fact]
    public void ShiftDragOverridesApplicationMouseTracking()
    {
        using var fixture = new SelectionPaneFixture("abcdef", tracking: 1003);
        fixture.Drag(new(1, 0), new(3, 0), MouseFlags.Shift);
        Assert.Equal("bcd", fixture.Pane.SelectedText);
        Assert.Empty(fixture.Input);
    }

    /// <summary>
    /// 验证：不按 Shift 时，1003 跟踪模式下的拖动（按下、拖动、释放）和随后的悬停移动都编码后转发给子程序，不产生本地选择。
    /// </summary>
    [Fact]
    public void ApplicationDragAndHoverRemainForwardedWithoutShift()
    {
        using var fixture = new SelectionPaneFixture("abcdef", tracking: 1003);
        fixture.Drag(new(1, 0), new(3, 0));
        fixture.Event(MouseFlags.PositionReport, new(4, 0));
        Assert.False(fixture.Pane.HasSelection);
        Assert.Equal(["\u001b[<0;2;1M", "\u001b[<32;4;1M", "\u001b[<0;4;1m", "\u001b[<35;5;1M"], fixture.Input);
    }

    /// <summary>
    /// 验证：拖动选择期间到达的屏幕输出不改变选中文本，选中的仍是按下时冻结的旧屏幕内容（<c>bcd</c>）；<c>ClearSelection</c> 后回到实时屏幕，重新选择得到新内容 <c>VWX</c>。
    /// </summary>
    [Fact]
    public void OutputUpdateDoesNotChangeSelectedTextAndClearReturnsToLiveScreen()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        fixture.Event(MouseFlags.LeftButtonPressed, new(1, 0));
        fixture.Pane.Update(Screen("UVWXYZ"));
        fixture.Event(MouseFlags.PositionReport | MouseFlags.LeftButtonPressed, new(3, 0));
        fixture.Event(MouseFlags.LeftButtonReleased, new(3, 0));
        Assert.Equal("bcd", fixture.Pane.SelectedText);
        fixture.Pane.ClearSelection();
        Assert.False(fixture.Pane.HasSelection);
        fixture.Drag(new(1, 0), new(3, 0));
        Assert.Equal("VWX", fixture.Pane.SelectedText);
    }

    /// <summary>
    /// 验证：未启用跟踪时滚轮向上在本地把历史滚动 3 行且不向子程序发送输入；滚动期间到达的新输出不改变显示的冻结历史（仍能选中 <c>zero</c>）；
    /// <c>ScrollToBottom</c> 后回到实时输出，可选中新内容 <c>new</c>。
    /// </summary>
    [Fact]
    public void WheelScrollsFrozenHistoryLocallyAndBottomResumesLiveOutput()
    {
        using var fixture = new SelectionPaneFixture("zero\r\none\r\ntwo\r\nthree\r\nfour\r\nfive");
        fixture.Event(MouseFlags.WheeledUp, new(2, 1));
        Assert.Equal(3, fixture.Pane.ScrollOffset);
        fixture.Pane.Update(Screen("new output"));
        fixture.Drag(new(0, 0), new(3, 0));
        Assert.Equal("zero", fixture.Pane.SelectedText);
        Assert.Empty(fixture.Input);
        fixture.Pane.ScrollToBottom();
        Assert.Equal(0, fixture.Pane.ScrollOffset);
        fixture.Drag(new(0, 0), new(2, 0));
        Assert.Equal("new", fixture.Pane.SelectedText);
    }

    /// <summary>
    /// 验证：子程序启用鼠标跟踪时，Shift+滚轮在本地把历史滚动 3 行，不转发给子程序。
    /// </summary>
    [Fact]
    public void ShiftWheelUsesHistoryWhenApplicationTracksMouse()
    {
        using var fixture = new SelectionPaneFixture("zero\r\none\r\ntwo\r\nthree\r\nfour\r\nfive", tracking: 1000);
        fixture.Event(MouseFlags.Shift | MouseFlags.WheeledUp, new(2, 1));
        Assert.Equal(3, fixture.Pane.ScrollOffset);
        Assert.Empty(fixture.Input);
    }

    /// <summary>
    /// 验证：未启用跟踪时，右键按下、释放和合成的 <c>RightButtonClicked</c> 只以屏幕坐标 (7,3) 请求一次上下文菜单，且不发送输入。
    /// </summary>
    [Fact]
    public void RightClickRequestsContextAtScreenCoordinatesWithoutDuplicateSyntheticClick()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        var positions = new List<Point>();
        fixture.Pane.ContextMenuRequested = positions.Add;
        fixture.Event(MouseFlags.RightButtonPressed, new(2, 1));
        fixture.Event(MouseFlags.RightButtonReleased, new(2, 1));
        fixture.Event(MouseFlags.RightButtonClicked, new(2, 1));
        Assert.Equal([new Point(7, 3)], positions);
        Assert.Empty(fixture.Input);
    }

    /// <summary>
    /// 验证：已有选择时，不拖动的单击会清除选择且不产生新的复制（复制记录仍只有先前拖动产生的一条）。
    /// </summary>
    [Fact]
    public void ClickWithoutDragClearsSelectionAndDoesNotCopy()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        fixture.Drag(new(1, 0), new(3, 0));
        fixture.Event(MouseFlags.LeftButtonPressed, new(2, 0));
        fixture.Event(MouseFlags.LeftButtonReleased, new(2, 0));
        Assert.False(fixture.Pane.HasSelection);
        Assert.Equal(["bcd"], fixture.Copies);
    }

    /// <summary>
    /// 验证：关闭 <c>CopyOnSelect</c> 后拖动仍能选中文本但不会自动复制；显式调用 <c>CopySelection</c> 才复制。
    /// </summary>
    [Fact]
    public void CopyOnSelectCanBeDisabledWithoutLosingSelection()
    {
        using var fixture = new SelectionPaneFixture("abcdef");
        fixture.Pane.CopyOnSelect = false;
        fixture.Drag(new(1, 0), new(3, 0));
        Assert.Equal("bcd", fixture.Pane.SelectedText);
        Assert.Empty(fixture.Copies);
        fixture.Pane.CopySelection();
        Assert.Equal(["bcd"], fixture.Copies);
    }

    /// <summary>
    /// 构造一个 10×3 的屏幕并送入指定文本，返回其快照，同时设置子程序的鼠标跟踪模式并启用 SGR 编码。
    /// </summary>
    /// <param name="text">送入屏幕的输出文本（可含 CRLF 换行）。</param>
    /// <param name="tracking">鼠标跟踪模式；0 表示未启用。</param>
    /// <returns>屏幕快照。</returns>
    internal static TerminalSnapshot Screen(string text, int tracking = 0)
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed(text);
        return screen.Snapshot() with { MouseTracking = tracking, SgrMouse = true };
    }
}
