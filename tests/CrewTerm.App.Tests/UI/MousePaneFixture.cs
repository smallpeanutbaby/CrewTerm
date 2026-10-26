using System.Drawing;
using CrewTerm.App.UI.Controls;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 鼠标路由测试用的宿主：并排放置两个已启用鼠标跟踪的终端面板，并记录各自收到的输入。
/// </summary>
internal sealed class MousePaneFixture : IDisposable
{
    /// <summary>
    /// 承载两个面板的父视图。
    /// </summary>
    private readonly View host = new() { Width = 100, Height = 40, CanFocus = true };

    /// <summary>
    /// 左侧面板。
    /// </summary>
    public TerminalPane First { get; } = new() { X = 1, Y = 2, Width = 40, Height = 12, SessionId = "first" };

    /// <summary>
    /// 右侧面板。
    /// </summary>
    public TerminalPane Second { get; } = new() { X = 50, Y = 2, Width = 40, Height = 12, SessionId = "second" };

    /// <summary>
    /// 左侧面板发送给子程序的输入。
    /// </summary>
    public List<string> FirstInput { get; } = [];

    /// <summary>
    /// 右侧面板发送给子程序的输入。
    /// </summary>
    public List<string> SecondInput { get; } = [];

    /// <summary>
    /// 创建并布局两个面板。
    /// </summary>
    public MousePaneFixture()
    {
        First.SendInput = FirstInput.Add;
        Second.SendInput = SecondInput.Add;
        First.Update(TerminalMouseTests.Snapshot());
        Second.Update(TerminalMouseTests.Snapshot());
        host.Add(First, Second);
        host.Layout(new Size(100, 40));
    }

    /// <summary>
    /// 构造一个落在右侧面板内（局部坐标 2,3）的鼠标事件。
    /// </summary>
    /// <param name="flags">鼠标按键与修饰键。</param>
    /// <returns>鼠标事件。</returns>
    public Mouse Mouse(MouseFlags flags) => new()
    {
        View = Second, Position = new Point(2, 3), ScreenPosition = new Point(52, 5), Flags = flags
    };

    /// <summary>
    /// 释放宿主视图及其面板。
    /// </summary>
    public void Dispose() => host.Dispose();
}
