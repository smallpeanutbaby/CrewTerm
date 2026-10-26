using System.Drawing;
using CrewTerm.App.UI.Controls;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalPane"/> 在尚无屏幕快照（空面板）时的鼠标处理单元测试：左键获得焦点与右键请求上下文菜单。
/// </summary>
public sealed class TerminalPaneContextTests
{
    /// <summary>
    /// 验证：空面板被左键点击时从另一面板夺得焦点，但不发送任何输入，也不产生选择。
    /// </summary>
    [Fact]
    public void EmptyPaneCanReceiveFocusFromALeftClickWithoutSendingInput()
    {
        using var host = new View { Width = 100, Height = 30, CanFocus = true };
        var other = new TerminalPane { X = 0, Y = 0, Width = 40, Height = 10 };
        var empty = new TerminalPane { X = 45, Y = 0, Width = 40, Height = 10 };
        var input = new List<string>();
        empty.SendInput = input.Add;
        host.Add(other, empty); host.Layout(new Size(100, 30));
        Assert.True(other.SetFocus());

        empty.NewMouseEvent(new Mouse { View = empty, Position = new(2, 3), ScreenPosition = new(47, 3), Flags = MouseFlags.LeftButtonPressed });

        Assert.True(empty.HasFocus);
        Assert.False(other.HasFocus);
        Assert.Empty(input);
        Assert.False(empty.HasSelection);
    }

    /// <summary>
    /// 验证：空面板依次收到右键按下、释放和合成的 <c>RightButtonClicked</c> 时，只以屏幕坐标 (47,5) 请求一次上下文菜单，同时获得焦点且不发送输入。
    /// </summary>
    [Fact]
    public void EmptyPaneRightClickRequestsOneMenuAtScreenCoordinatesWithoutSendingInput()
    {
        using var host = new View { Width = 100, Height = 30, CanFocus = true };
        var empty = new TerminalPane { X = 45, Y = 2, Width = 40, Height = 10 };
        var positions = new List<Point>();
        var input = new List<string>();
        empty.SendInput = input.Add;
        empty.ContextMenuRequested = positions.Add;
        host.Add(empty); host.Layout(new Size(100, 30));

        foreach (var flags in new[] { MouseFlags.RightButtonPressed, MouseFlags.RightButtonReleased, MouseFlags.RightButtonClicked })
            empty.NewMouseEvent(new Mouse { View = empty, Position = new(2, 3), ScreenPosition = new(47, 5), Flags = flags });

        Assert.Equal([new Point(47, 5)], positions);
        Assert.True(empty.HasFocus);
        Assert.Empty(input);
    }
}
