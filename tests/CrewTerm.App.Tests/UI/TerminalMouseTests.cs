using System.Drawing;
using CrewTerm.App.UI.Controls;
using CrewTerm.App.UI.Input;
using CrewTerm.Terminal.Emulation;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalMouse.Encode"/>（把 Terminal.Gui 鼠标事件编码为子程序所请求的鼠标报告）与 <see cref="TerminalPane"/> 鼠标路由的单元测试：
/// 覆盖 SGR 与传统 X10 编码、跟踪模式、坐标边界、UTF-8 扩展坐标，以及两个面板之间的焦点与滚轮目标。
/// </summary>
public sealed class TerminalMouseTests
{
    /// <summary>
    /// 验证：SGR 模式下，各按键的按下与释放（以结尾 <c>M</c> / <c>m</c> 区分）、四个滚轮方向以及 Shift/Ctrl/Alt 修饰键都编码为正确的按钮码，
    /// 坐标为面板局部坐标 (2,3) 换算成的从 1 开始的 <c>3;4</c>。
    /// </summary>
    /// <param name="flags">鼠标按键、动作与修饰键。</param>
    /// <param name="expected">期望的 SGR 序列。</param>
    [Theory]
    [InlineData(MouseFlags.LeftButtonPressed, "\u001b[<0;3;4M")]
    [InlineData(MouseFlags.LeftButtonReleased, "\u001b[<0;3;4m")]
    [InlineData(MouseFlags.MiddleButtonPressed, "\u001b[<1;3;4M")]
    [InlineData(MouseFlags.MiddleButtonReleased, "\u001b[<1;3;4m")]
    [InlineData(MouseFlags.RightButtonPressed, "\u001b[<2;3;4M")]
    [InlineData(MouseFlags.RightButtonReleased, "\u001b[<2;3;4m")]
    [InlineData(MouseFlags.WheeledUp, "\u001b[<64;3;4M")]
    [InlineData(MouseFlags.WheeledDown, "\u001b[<65;3;4M")]
    // Terminal.Gui 把 Ctrl+滚轮映射为 WheeledLeft/WheeledRight，因此这两个标志表示水平滚动。
    [InlineData(MouseFlags.WheeledLeft, "\u001b[<66;3;4M")]
    [InlineData(MouseFlags.WheeledRight, "\u001b[<67;3;4M")]
    [InlineData(MouseFlags.LeftButtonPressed | MouseFlags.Shift | MouseFlags.Ctrl, "\u001b[<20;3;4M")]
    [InlineData(MouseFlags.RightButtonReleased | MouseFlags.Alt, "\u001b[<10;3;4m")]
    public void SgrEncodingPreservesButtonDirectionAndModifiers(MouseFlags flags, string expected)
    {
        var mouse = new Mouse { Position = new Point(2, 3), ScreenPosition = new Point(52, 5), Flags = flags };

        Assert.Equal(expected, TerminalMouse.Encode(mouse, Snapshot()));
    }

    /// <summary>
    /// 验证：子程序未启用鼠标跟踪（0）或使用不支持的跟踪模式（1001）时，不产生任何鼠标报告。
    /// </summary>
    /// <param name="tracking">子程序启用的鼠标跟踪模式。</param>
    /// <param name="sgr">子程序是否启用了 SGR 扩展编码。</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1001, true)]
    public void EncodingRequiresSupportedTracking(int tracking, bool sgr)
    {
        var mouse = new Mouse { Position = new Point(0, 0), Flags = MouseFlags.LeftButtonPressed };

        Assert.Null(TerminalMouse.Encode(mouse, Snapshot(tracking, sgr)));
    }

    /// <summary>
    /// 验证：1000、1002、1003 三种受支持的跟踪模式都能报告左键按下。
    /// </summary>
    /// <param name="tracking">子程序启用的鼠标跟踪模式。</param>
    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void AllSupportedTrackingModesCanReportClicks(int tracking)
    {
        var mouse = new Mouse { Position = new Point(0, 0), Flags = MouseFlags.LeftButtonPressed };

        Assert.Equal("\u001b[<0;1;1M", TerminalMouse.Encode(mouse, Snapshot(tracking)));
    }

    /// <summary>
    /// 验证：Terminal.Gui 合成的单击、双击、三击事件，以及 1000 模式下的移动报告（包括按住左键移动）都不编码，避免向子程序重复发送按下事件。
    /// </summary>
    /// <param name="flags">合成的点击事件或移动报告标志。</param>
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

    /// <summary>
    /// 验证：坐标超出 80×25 子屏幕范围（为负或等于宽度/高度）时不编码。
    /// </summary>
    /// <param name="x">面板局部列坐标（从 0 开始）。</param>
    /// <param name="y">面板局部行坐标（从 0 开始）。</param>
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

    /// <summary>
    /// 验证：子屏幕最后一个单元格 (79,24) 编码为从 1 开始的坐标 <c>80;25</c>。
    /// </summary>
    [Fact]
    public void LastScreenCellUsesOneBasedCoordinates()
    {
        var mouse = new Mouse { Position = new Point(79, 24), Flags = MouseFlags.WheeledDown };

        Assert.Equal("\u001b[<65;80;25M", TerminalMouse.Encode(mouse, Snapshot()));
    }

    /// <summary>
    /// 验证：没有屏幕快照，或鼠标事件缺少位置时，不产生任何输入。
    /// </summary>
    [Fact]
    public void MissingSnapshotOrPositionDoesNotEmitInput()
    {
        Assert.Null(TerminalMouse.Encode(new Mouse { Position = new Point(0, 0), Flags = MouseFlags.WheeledDown }, null));
        Assert.Null(TerminalMouse.Encode(new Mouse { Flags = MouseFlags.LeftButtonPressed }, Snapshot()));
    }

    /// <summary>
    /// 验证：焦点在左侧面板时点击右侧面板，焦点移到右侧面板，并只向右侧子程序发送一次按下和一次释放（按局部坐标编码）；
    /// 随后合成的 <c>LeftButtonClicked</c> 不会重复发送，左侧面板收不到任何输入。
    /// </summary>
    [Fact]
    public void ClickingTheSecondPaneFocusesItAndSendsOnePressAndReleaseInLocalCoordinates()
    {
        using var panes = new MousePaneFixture();
        panes.First.SetFocus();
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonPressed));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonReleased));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonClicked));

        Assert.Equal(["\u001b[<0;3;4M", "\u001b[<0;3;4m"], panes.SecondInput);
        Assert.Empty(panes.FirstInput);
        Assert.True(panes.Second.HasFocus);
        Assert.False(panes.First.HasFocus);
    }

    /// <summary>
    /// 验证：滚轮事件发送给指针下方的右侧面板，而键盘焦点仍留在左侧面板。
    /// </summary>
    [Fact]
    public void WheelTargetsThePaneUnderThePointerWithoutChangingKeyboardFocus()
    {
        using var panes = new MousePaneFixture();
        panes.First.SetFocus();

        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledUp));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledDown));

        Assert.Equal(["\u001b[<64;3;4M", "\u001b[<65;3;4M"], panes.SecondInput);
        Assert.Empty(panes.FirstInput);
        Assert.True(panes.First.HasFocus);
    }

    /// <summary>
    /// 验证：右侧子程序未启用鼠标跟踪时，点击和滚轮只把焦点移到右侧面板，不向任何子程序发送输入。
    /// </summary>
    /// <param name="tracking">右侧子程序的鼠标跟踪模式。</param>
    /// <param name="sgr">右侧子程序是否启用了 SGR 扩展编码。</param>
    [Theory]
    [InlineData(0, true)]
    public void ClickingWithoutTrackingOnlyChangesFocus(int tracking, bool sgr)
    {
        using var panes = new MousePaneFixture();
        panes.Second.Update(Snapshot(tracking, sgr));
        panes.First.SetFocus();

        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonPressed));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.LeftButtonReleased));
        panes.Second.NewMouseEvent(panes.Mouse(MouseFlags.WheeledDown));

        Assert.True(panes.Second.HasFocus);
        Assert.Empty(panes.SecondInput);
        Assert.Empty(panes.FirstInput);
    }

    /// <summary>
    /// 验证：未启用 SGR 时使用传统 X10 格式（<c>ESC[M</c> 后接按钮码与坐标各加 32 的三个字节），正确编码按下、带 Ctrl 的释放（释放统一为按钮码 3）和水平滚轮。
    /// </summary>
    /// <param name="flags">鼠标按键、动作与修饰键。</param>
    /// <param name="expected">期望的传统格式序列。</param>
    [Theory]
    [InlineData(MouseFlags.LeftButtonPressed, "\u001b[M #$")]
    [InlineData(MouseFlags.RightButtonReleased | MouseFlags.Ctrl, "\u001b[M3#$")]
    [InlineData(MouseFlags.WheeledRight, "\u001b[Mc#$")]
    public void LegacyProtocolEncodesPressReleaseAndWheel(MouseFlags flags, string expected)
    {
        Assert.Equal(expected, TerminalMouse.Encode(new Mouse { Position = new(2, 3), Flags = flags }, Snapshot(1000, false)));
    }

    /// <summary>
    /// 验证：移动报告遵守跟踪模式：1000 不报告移动；1002 只报告按住按键时的拖动（按钮码加 32 并保留修饰键）；1003 还报告未按键的移动（按钮码 35）。
    /// </summary>
    /// <param name="tracking">子程序启用的鼠标跟踪模式。</param>
    /// <param name="flags">移动报告及按住的按键、修饰键。</param>
    /// <param name="expected">期望的 SGR 序列；为 null 表示不应报告。</param>
    [Theory]
    [InlineData(1000, MouseFlags.PositionReport | MouseFlags.LeftButtonPressed, null)]
    [InlineData(1002, MouseFlags.PositionReport, null)]
    [InlineData(1002, MouseFlags.PositionReport | MouseFlags.LeftButtonPressed, "\u001b[<32;3;4M")]
    [InlineData(1002, MouseFlags.PositionReport | MouseFlags.RightButtonPressed | MouseFlags.Alt, "\u001b[<42;3;4M")]
    [InlineData(1003, MouseFlags.PositionReport, "\u001b[<35;3;4M")]
    [InlineData(1003, MouseFlags.PositionReport | MouseFlags.MiddleButtonPressed, "\u001b[<33;3;4M")]
    public void MotionHonorsTrackingModeAndHeldButton(int tracking, MouseFlags flags, string? expected)
    {
        Assert.Equal(expected, TerminalMouse.Encode(new Mouse { Position = new(2, 3), Flags = flags }, Snapshot(tracking)));
    }

    /// <summary>
    /// 验证：传统格式下坐标以单个字节表示，最多可编码到第 223 列（字节 0xFF）；更远的位置直接不编码，而不是回绕成错误坐标。
    /// </summary>
    [Fact]
    public void LegacyCoordinatesAreByteLimitedRatherThanWrapping()
    {
        var snapshot = new TerminalScreen(300, 25).Snapshot() with { MouseTracking = 1000 };
        Assert.Equal("\u001b[M \u00ff!", TerminalMouse.Encode(new Mouse { Position = new(222, 0), Flags = MouseFlags.LeftButtonPressed }, snapshot));
        Assert.Null(TerminalMouse.Encode(new Mouse { Position = new(223, 0), Flags = MouseFlags.LeftButtonPressed }, snapshot));
    }

    /// <summary>
    /// 验证：子程序启用 1005（UTF-8 鼠标坐标）模式后，超出单字节上限的坐标（第 301 列）以对应的 UTF-8 字符编码。
    /// </summary>
    [Fact]
    public void Utf8MouseModeAllowsCoordinatesBeyondLegacyByteLimit()
    {
        var screen = new TerminalScreen(400, 25);
        screen.Feed("\u001b[?1000;1005h");
        Assert.True(screen.Snapshot().Utf8Mouse);
        Assert.Equal("\u001b[M \u014d!", TerminalMouse.Encode(new Mouse { Position = new(300, 0), Flags = MouseFlags.LeftButtonPressed }, screen.Snapshot()));
    }

    /// <summary>
    /// 验证：落在面板视口之外（局部列 41，而面板宽 40）的鼠标事件不会转发给子程序。
    /// </summary>
    [Fact]
    public void MouseOutsideThePaneViewportIsNotForwarded()
    {
        using var panes = new MousePaneFixture();

        var mouse = panes.Mouse(MouseFlags.WheeledDown);
        mouse.Position = new Point(41, 3);
        panes.Second.NewMouseEvent(mouse);

        Assert.Empty(panes.SecondInput);
    }

    /// <summary>
    /// 构造一个 80×25 的空白屏幕快照，并设置子程序的鼠标跟踪模式与 SGR 开关。
    /// </summary>
    /// <param name="tracking">鼠标跟踪模式；默认 1000（仅按键）。</param>
    /// <param name="sgr">是否启用 SGR 扩展编码。</param>
    /// <returns>屏幕快照。</returns>
    internal static TerminalSnapshot Snapshot(int tracking = 1000, bool sgr = true) =>
        new TerminalScreen(80, 25).Snapshot() with { MouseTracking = tracking, SgrMouse = sgr };
}
