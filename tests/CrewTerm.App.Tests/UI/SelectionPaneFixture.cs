using System.Drawing;
using CrewTerm.App.UI.Controls;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 文本选择测试用的宿主：放置一个显示指定文本的终端面板，并记录复制请求与发送的输入。
/// </summary>
internal sealed class SelectionPaneFixture : IDisposable
{
    /// <summary>
    /// 承载面板的父视图。
    /// </summary>
    private readonly View host = new() { Width = 30, Height = 10, CanFocus = true };

    /// <summary>
    /// 被测面板（10×3，位于父视图的 5,2）。
    /// </summary>
    public TerminalPane Pane { get; } = new() { X = 5, Y = 2, Width = 10, Height = 3, SessionId = "selection" };

    /// <summary>
    /// 面板请求复制到剪贴板的文本。
    /// </summary>
    public List<string> Copies { get; } = [];

    /// <summary>
    /// 面板发送给子程序的输入。
    /// </summary>
    public List<string> Input { get; } = [];

    /// <summary>
    /// 创建面板并显示文本。
    /// </summary>
    /// <param name="text">送入屏幕的输出文本。</param>
    /// <param name="tracking">子程序启用的鼠标跟踪模式；0 表示未启用。</param>
    public SelectionPaneFixture(string text, int tracking = 0)
    {
        Pane.CopyRequested = Copies.Add;
        Pane.SendInput = Input.Add;
        Pane.Update(TerminalPaneSelectionTests.Screen(text, tracking));
        host.Add(Pane);
        host.Layout(new Size(30, 10));
    }

    /// <summary>
    /// 向面板发送一个鼠标事件。
    /// </summary>
    /// <param name="flags">鼠标按键与修饰键。</param>
    /// <param name="point">面板局部坐标。</param>
    public void Event(MouseFlags flags, Point point) => Pane.NewMouseEvent(new Mouse
    {
        View = Pane, Position = point, ScreenPosition = new(point.X + 5, point.Y + 2), Flags = flags
    });

    /// <summary>
    /// 模拟一次左键拖动：按下、移动、释放。
    /// </summary>
    /// <param name="start">起点（局部坐标）。</param>
    /// <param name="end">终点（局部坐标）。</param>
    /// <param name="modifiers">附加的修饰键，例如 Shift。</param>
    public void Drag(Point start, Point end, MouseFlags modifiers = MouseFlags.None)
    {
        Event(MouseFlags.LeftButtonPressed | modifiers, start);
        Event(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport | modifiers, end);
        Event(MouseFlags.LeftButtonReleased | modifiers, end);
    }

    /// <summary>
    /// 释放宿主视图及其面板。
    /// </summary>
    public void Dispose() => host.Dispose();
}
