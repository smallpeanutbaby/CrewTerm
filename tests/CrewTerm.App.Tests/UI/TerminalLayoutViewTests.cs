using System.Drawing;
using CrewTerm.App.UI.Controls;
using CrewTerm.Core.Layouts;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalLayoutView"/>（标签栏加可嵌套分割的终端面板容器）的单元测试：在内存中布局 Terminal.Gui 视图并直接投递鼠标事件，
/// 验证切换标签或项目时旧面板的释放、拖动分隔线、点击标签、焦点穿透布局容器以及标签控件的高度。
/// </summary>
public sealed class TerminalLayoutViewTests
{
    /// <summary>
    /// 验证：焦点位于含两个分割面板的标签中时，切换到另一个标签或换成另一个项目的布局后，被释放的旧面板不会在失去焦点时改写新的选择：
    /// 活动标签与活动面板指向新的目标（会话 <c>c</c> 或 <c>d</c>），视图中只剩一个面板。
    /// </summary>
    /// <param name="changeProject">为 true 时换成另一个项目的布局；为 false 时切换到同一布局中的另一个标签。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingFocusedSplitDoesNotLetDisposedPanesChangeNewSelection(bool changeProject)
    {
        using var window = new Window { Width = 120, Height = 40 };
        var layout = new TerminalLayout();
        var first = layout.AddTab("First", "a");
        layout.Split(first.Root.Id, SplitDirection.Right, "b");
        var firstPane = first.Panes.First().Id;
        var second = layout.AddTab("Second", "c");
        layout.SelectPane(firstPane);
        var view = new TerminalLayoutView(); view.ShowLayout(layout);
        window.Add(view); window.BeginInit(); window.EndInit(); window.Layout(new Size(120, 40));
        view.ActivePane.SetFocus();
        if (changeProject)
        {
            var replacement = new TerminalLayout(); replacement.AddTab("Other project", "d");
            view.ShowLayout(replacement);
            Assert.Same(replacement, view.LayoutModel);
            Assert.Equal("d", view.ActivePane.SessionId);
        }
        else
        {
            view.SelectTab(second.Id);
            Assert.Equal(second.Id, layout.ActiveTabId);
            Assert.Equal("c", view.ActivePane.SessionId);
        }
        Assert.Single(view.Panes);
    }

    /// <summary>
    /// 验证：在 101×42 的视图中按住分隔线并移动指定距离后，只有该分割节点的比例变为约 0.75，面板及其会话顺序（<c>a</c>、<c>b</c>）保持不变。
    /// </summary>
    /// <param name="direction">分割方向。</param>
    /// <param name="dx">拖动的水平距离（列）。</param>
    /// <param name="dy">拖动的垂直距离（行）。</param>
    [Theory]
    [InlineData(SplitDirection.Right, 25, 0)]
    [InlineData(SplitDirection.Down, 0, 10)]
    public void DraggingDividerUpdatesOnlyItsSplitRatio(SplitDirection direction, int dx, int dy)
    {
        using var view = new TerminalLayoutView { Width = 101, Height = 42 };
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main", "a");
        layout.Split(tab.Root.Id, direction, "b");
        view.ShowLayout(layout); view.BeginInit(); view.EndInit(); view.Layout(new Size(101, 42));
        var divider = view.SubViews.ElementAt(1).SubViews.Single(v => v.Id == tab.Root.Id);
        divider.NewMouseEvent(new Mouse { Position = new(0, 0), Flags = MouseFlags.LeftButtonPressed });
        divider.NewMouseEvent(new Mouse { Position = new(dx, dy), Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport });
        Assert.Equal(0.75, tab.Root.Ratio, 2);
        Assert.Equal(new[] { "a", "b" }, tab.Panes.Select(p => p.SessionId));
    }

    /// <summary>
    /// 验证：添加两个标签后，点击标签栏中的第一个标签会激活 First，并显示它保存的面板（会话 <c>a</c>）。
    /// </summary>
    [Fact]
    public void ClickingNamedTabSelectsItsSavedPane()
    {
        using var view = new TerminalLayoutView { Width = 120, Height = 40 };
        var layout = new TerminalLayout();
        var first = layout.AddTab("First", "a");
        layout.AddTab("Second", "b");
        view.ShowLayout(layout); view.BeginInit(); view.EndInit(); view.Layout(new Size(120, 40));
        view.SubViews.First().SubViews.First().NewMouseEvent(new Mouse { Position = new(2, 0), Flags = MouseFlags.LeftButtonPressed });
        Assert.Equal(first.Id, layout.ActiveTabId);
        Assert.Equal("a", view.ActivePane.SessionId);
    }

    /// <summary>
    /// 验证：向下分割后的活动面板（会话 <c>b</c>）能够穿过各层布局容器获得键盘焦点。
    /// </summary>
    [Fact]
    public void SplitPaneCanReceiveKeyboardFocusThroughLayoutContainers()
    {
        using var window = new Window { Width = 120, Height = 40 };
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Terminal", "a");
        layout.Split(tab.Root.Id, SplitDirection.Down, "b");
        var view = new TerminalLayoutView();
        view.ShowLayout(layout); window.Add(view);
        window.BeginInit(); window.EndInit(); window.Layout(new Size(120, 40));
        Assert.True(view.ActivePane.SetFocus());
        Assert.True(view.ActivePane.HasFocus);
        Assert.Equal("b", view.ActivePane.SessionId);
    }

    /// <summary>
    /// 验证：标签栏中存在控件，且每个控件的视口高度都为 1 行，保证标签文字有可绘制的行。
    /// </summary>
    [Fact]
    public void TabControlsHaveADrawableTextRow()
    {
        using var view = new TerminalLayoutView { Width = 120, Height = 40 };
        var layout = new TerminalLayout();
        layout.AddTab("Review"); view.ShowLayout(layout);
        view.BeginInit(); view.EndInit(); view.Layout(new Size(120, 40));
        var controls = view.SubViews.First().SubViews.ToArray();
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.Equal(1, control.Viewport.Height));
    }
}
