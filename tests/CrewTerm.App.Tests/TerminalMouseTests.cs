using System.Drawing;
using CrewTerm.App.UI;
using CrewTerm.Terminal;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.Tests;

public sealed class TerminalMouseTests
{
    [Theory]
    [InlineData(MouseFlags.LeftButtonPressed, "\u001b[<0;3;4M")]
    [InlineData(MouseFlags.LeftButtonReleased, "\u001b[<0;3;4m")]
    [InlineData(MouseFlags.MiddleButtonPressed, "\u001b[<1;3;4M")]
    [InlineData(MouseFlags.MiddleButtonReleased, "\u001b[<1;3;4m")]
    [InlineData(MouseFlags.RightButtonPressed, "\u001b[<2;3;4M")]
    [InlineData(MouseFlags.RightButtonReleased, "\u001b[<2;3;4m")]
    [InlineData(MouseFlags.WheeledUp, "\u001b[<64;3;4M")]
    [InlineData(MouseFlags.WheeledDown, "\u001b[<65;3;4M")]
    [InlineData(MouseFlags.LeftButtonPressed | MouseFlags.Shift | MouseFlags.Ctrl, "\u001b[<20;3;4M")]
    [InlineData(MouseFlags.RightButtonReleased | MouseFlags.Alt, "\u001b[<10;3;4m")]
    [InlineData(MouseFlags.WheeledUp | MouseFlags.Ctrl, "\u001b[<80;3;4M")]
    public void SgrEncodingPreservesButtonDirectionAndModifiers(MouseFlags flags, string expected)
    {
        var mouse = new Mouse { Position = new Point(2, 3), ScreenPosition = new Point(52, 5), Flags = flags };

        Assert.Equal(expected, TerminalMouse.Encode(mouse, Snapshot()));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public void EncodingRequiresSupportedTrackingAndSgrMode(int tracking, bool sgr)
    {
        var mouse = new Mouse { Position = new Point(0, 0), Flags = MouseFlags.LeftButtonPressed };

        Assert.Null(TerminalMouse.Encode(mouse, Snapshot(tracking, sgr)));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void AllSupportedTrackingModesCanReportClicks(int tracking)
    {
        var mouse = new Mouse { Position = new Point(0, 0), Flags = MouseFlags.LeftButtonPressed };

        Assert.Equal("\u001b[<0;1;1M", TerminalMouse.Encode(mouse, Snapshot(tracking)));
    }

    [Theory]
    [InlineData(MouseFlags.LeftButtonClicked)]
    [InlineData(MouseFlags.LeftButtonDoubleClicked)]
    [InlineData(MouseFlags.RightButtonTripleClicked)]
    [InlineData(MouseFlags.PositionReport)]
    [InlineData(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport)]
    public void SyntheticClicksAndMotionDoNotGenerateDuplicatePresses(MouseFlags flags)
    {
        var mouse = new Mouse { Position = new Point(0, 0), Flags = flags };

        Assert.Null(TerminalMouse.Encode(mouse, Snapshot()));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(80, 0)]
    [InlineData(0, 25)]
    public void PositionsOutsideTheChildScreenAreNotEncoded(int x, int y)
    {
        var mouse = new Mouse { Position = new Point(x, y), Flags = MouseFlags.WheeledDown };

        Assert.Null(TerminalMouse.Encode(mouse, Snapshot()));
    }

    [Fact]
    public void LastScreenCellUsesOneBasedCoordinates()
    {
        var mouse = new Mouse { Position = new Point(79, 24), Flags = MouseFlags.WheeledDown };

        Assert.Equal("\u001b[<65;80;25M", TerminalMouse.Encode(mouse, Snapshot()));
    }

    [Fact]
    public void MissingSnapshotOrPositionDoesNotEmitInput()
    {
        Assert.Null(TerminalMouse.Encode(new Mouse { Position = new Point(0, 0), Flags = MouseFlags.WheeledDown }, null));
        Assert.Null(TerminalMouse.Encode(new Mouse { Flags = MouseFlags.LeftButtonPressed }, Snapshot()));
    }

    [Fact]
    public void ClickingTheSecondPaneFocusesItAndSendsOnePressAndReleaseInLocalCoordinates()
    {
        using var panes = new PaneFixture();
        panes.First.SetFocus();
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonPressed));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonReleased));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonClicked));

        Assert.Equal(["\u001b[<0;3;4M", "\u001b[<0;3;4m"], panes.SecondInput);
        Assert.Empty(panes.FirstInput);
        Assert.True(panes.Second.HasFocus);
        Assert.False(panes.First.HasFocus);
    }

    [Fact]
    public void WheelTargetsThePaneUnderThePointerWithoutChangingKeyboardFocus()
    {
        using var panes = new PaneFixture();
        panes.First.SetFocus();

        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledUp));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledDown));

        Assert.Equal(["\u001b[<64;3;4M", "\u001b[<65;3;4M"], panes.SecondInput);
        Assert.Empty(panes.FirstInput);
        Assert.True(panes.First.HasFocus);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, false)]
    public void ClickingWithoutSgrTrackingOnlyChangesFocus(int tracking, bool sgr)
    {
        using var panes = new PaneFixture();
        panes.Second.Update(Snapshot(tracking, sgr));
        panes.First.SetFocus();

        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonPressed));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonReleased));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledDown));

        Assert.True(panes.Second.HasFocus);
        Assert.Empty(panes.SecondInput);
        Assert.Empty(panes.FirstInput);
    }

    [Fact]
    public void MouseOutsideThePaneViewportIsNotForwarded()
    {
        using var panes = new PaneFixture();

        var mouse = panes.Mouse(MouseFlags.WheeledDown);
        mouse.Position = new Point(41, 3);
        panes.Second.NewMouseEvent(mouse);

        Assert.Empty(panes.SecondInput);
    }

    private static TerminalSnapshot Snapshot(int tracking = 1000, bool sgr = true) =>
        new TerminalScreen(80, 25).Snapshot() with { MouseTracking = tracking, SgrMouse = sgr };

    private sealed class PaneFixture : IDisposable
    {
        private readonly View host = new() { Width = 100, Height = 40, CanFocus = true };
        public TerminalPane First { get; } = new() { X = 1, Y = 2, Width = 40, Height = 12, SessionId = "first" };
        public TerminalPane Second { get; } = new() { X = 50, Y = 2, Width = 40, Height = 12, SessionId = "second" };
        public List<string> FirstInput { get; } = [];
        public List<string> SecondInput { get; } = [];

        public PaneFixture()
        {
            First.SendInput = FirstInput.Add;
            Second.SendInput = SecondInput.Add;
            First.Update(Snapshot());
            Second.Update(Snapshot());
            host.Add(First, Second);
            host.Layout(new Size(100, 40));
        }

        public Mouse Mouse(MouseFlags flags) => new()
        {
            View = Second, Position = new Point(2, 3), ScreenPosition = new Point(52, 5), Flags = flags
        };

        public void Dispose() => host.Dispose();
    }
}
