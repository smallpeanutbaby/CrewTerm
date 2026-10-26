using System.Text.Json;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Settings;

namespace CrewTerm.Core.Tests.Layouts;

/// <summary>
/// 覆盖 <see cref="TerminalLayout"/> 的标签页与分屏树操作、<see cref="LayoutGeometry"/> 的几何计算，
/// 以及布局经 <see cref="WorkspaceSettingsStore"/> 保存与读取时的校验和往返保真。
/// 大多数测试是纯内存单元测试；涉及设置存储的测试在系统临时目录中读写真实文件。
/// </summary>
public sealed class TerminalLayoutTests
{
    /// <summary>
    /// 验证：先向右再向下拆分原窗格后，形成“水平分割（左：垂直分割 a/c，右：b）”的嵌套树，
    /// 已有窗格的会话绑定保持不变，新拆出的窗格 c 获得焦点，标签页共有 3 个窗格。
    /// </summary>
    [Fact]
    public void NestedSplitsKeepExistingBindingsAndFocusNewPane()
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Build", "a");
        var original = tab.Root.Id;
        var right = layout.Split(original, SplitDirection.Right, "b");
        var bottom = layout.Split(original, SplitDirection.Down, "c");
        Assert.Equal(SplitAxis.Horizontal, tab.Root.Axis);
        Assert.Equal(SplitAxis.Vertical, tab.Root.First!.Axis);
        Assert.Equal("a", tab.Root.First.First!.SessionId);
        Assert.Equal("c", tab.Root.First.Second!.SessionId);
        Assert.Equal("b", tab.Root.Second!.SessionId);
        Assert.Equal(bottom.Id, tab.ActivePaneId);
        Assert.Equal(3, tab.Panes.Count());
        Assert.Equal(right.Id, tab.Root.Second.Id);
    }

    /// <summary>
    /// 验证：向左或向上拆分时，新窗格放在原窗格之前，分割轴与方向对应，窗格的显示顺序为“新窗格、原窗格”。
    /// </summary>
    /// <param name="direction">拆分方向（<see cref="SplitDirection.Left"/> 或 <see cref="SplitDirection.Up"/>）。</param>
    /// <param name="axis">该方向应产生的分割轴。</param>
    [Theory]
    [InlineData(SplitDirection.Left, SplitAxis.Horizontal)]
    [InlineData(SplitDirection.Up, SplitAxis.Vertical)]
    public void SplitBeforeSelectedPanePreservesVisualOrder(SplitDirection direction, SplitAxis axis)
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Review", "original");
        layout.Split(tab.Root.Id, direction, "new");
        Assert.Equal(axis, tab.Root.Axis);
        Assert.Equal(new[] { "new", "original" }, tab.Panes.Select(p => p.SessionId));
    }

    /// <summary>
    /// 验证：在“左侧 a/c 上下分割、右侧 b”的布局中关闭窗格 a，只会折叠 a 所在的父分割（c 顶替其位置），
    /// 其余窗格 c、b 仍绑定原会话，右侧仍是原来的 b 节点，且活动窗格指向仍存在的窗格。
    /// </summary>
    [Fact]
    public void ClosingPaneCollapsesOnlyItsParentAndLeavesSiblingProcessesBound()
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main", "a");
        var a = tab.Root.Id;
        var b = layout.Split(a, SplitDirection.Right, "b");
        layout.Split(a, SplitDirection.Down, "c");
        layout.ClosePane(a);
        Assert.Equal(new[] { "c", "b" }, tab.Panes.Select(p => p.SessionId));
        Assert.Equal(b.Id, tab.Root.Second!.Id);
        Assert.Contains(tab.Panes, p => p.Id == tab.ActivePaneId);
    }

    /// <summary>
    /// 验证：多个标签页各自维护独立的窗格树；重命名、按窗格选中标签页均生效；关闭活动标签页后切换到剩余标签页；
    /// 关闭最后一个标签页的唯一窗格后，会自动补建一个只含未绑定会话空白窗格的标签页，布局仍可使用。
    /// </summary>
    [Fact]
    public void TabsKeepIndependentTreesAndClosingLastTabLeavesUsableBlankPane()
    {
        var layout = new TerminalLayout();
        var first = layout.AddTab("One", "a");
        var second = layout.AddTab("Two", "b");
        layout.RenameTab(second.Id, "Review");
        layout.SelectPane(first.Root.Id);
        Assert.Equal(first.Id, layout.ActiveTabId);
        Assert.Equal("Review", second.Name);
        layout.CloseTab(first.Id);
        Assert.Equal(second.Id, layout.ActiveTabId);
        Assert.Equal("b", layout.ActiveTab.Root.SessionId);
        layout.ClosePane(second.Root.Id);
        Assert.Single(layout.Tabs);
        Assert.Null(Assert.Single(layout.ActiveTab.Panes).SessionId);
    }

    /// <summary>
    /// 验证：在 101×41 的区域中，把根分割比例拖到 0.6 后，<see cref="LayoutGeometry.Measure"/> 同时按水平与垂直两个轴分配空间：
    /// 左上 a 为 60×20、右侧 b 为 40×41、左下 c 为 60×20，各区域之间各留 1 格分隔线（共 2 条）且互不重叠。
    /// </summary>
    [Fact]
    public void GeometryUsesBothAxesAndDraggedRatioWithoutOverlappingCells()
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main", "a");
        var a = tab.Root.Id;
        var b = layout.Split(a, SplitDirection.Right, "b");
        var c = layout.Split(a, SplitDirection.Down, "c");
        layout.Resize(tab.Root.Id, 0.6);
        var geometry = LayoutGeometry.Measure(tab.Root, 101, 41);
        Assert.Equal(new LayoutRect(0, 0, 60, 20), geometry.Single(p => p.NodeId == a).Bounds);
        Assert.Equal(new LayoutRect(61, 0, 40, 41), geometry.Single(p => p.NodeId == b.Id).Bounds);
        Assert.Equal(new LayoutRect(0, 21, 60, 20), geometry.Single(p => p.NodeId == c.Id).Bounds);
        Assert.Equal(2, geometry.Count(p => p.IsDivider));
    }

    /// <summary>
    /// 验证：窗口极小（甚至为 0×0）时，三窗格布局计算出的每个区域（含分隔线）坐标和尺寸都不为负，且不超出窗口边界。
    /// </summary>
    /// <param name="width">窗口宽度（列数）。</param>
    /// <param name="height">窗口高度（行数）。</param>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public void TinyWindowsNeverProduceNegativeOrOutOfBoundsRegions(int width, int height)
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main");
        var a = tab.Root.Id;
        layout.Split(a, SplitDirection.Left);
        layout.Split(a, SplitDirection.Down);
        foreach (var item in LayoutGeometry.Measure(tab.Root, width, height))
        {
            Assert.InRange(item.Bounds.X, 0, width);
            Assert.InRange(item.Bounds.Y, 0, height);
            Assert.InRange(item.Bounds.Width, 0, width - item.Bounds.X);
            Assert.InRange(item.Bounds.Height, 0, height - item.Bounds.Y);
        }
    }

    /// <summary>
    /// 验证：一个标签页已有 16 个窗格（上限）时，再次拆分抛出 <see cref="InvalidOperationException"/>，
    /// 现有树保持 16 个窗格不变，也不会出现绑定 <c>extra</c> 会话的窗格。
    /// </summary>
    [Fact]
    public void ExcessiveSplitsAreRejectedWithoutChangingExistingTree()
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main", "a");
        var a = tab.Root.Id;
        for (var i = 1; i < 16; i++) layout.Split(a, SplitDirection.Right, i.ToString());
        Assert.Throws<InvalidOperationException>(() => layout.Split(a, SplitDirection.Down, "extra"));
        Assert.Equal(16, tab.Panes.Count());
        Assert.DoesNotContain(tab.Panes, p => p.SessionId == "extra");
    }

    /// <summary>
    /// 验证：以非有限值或超出 0.1～0.9 范围的比例调整分隔线时抛出 <see cref="ArgumentOutOfRangeException"/>，
    /// 分割节点保持默认比例 0.5，不会把非法值写入将被持久化的布局。
    /// </summary>
    /// <param name="ratio">非法的分割比例（<see cref="double.NaN"/>、0 或 1）。</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(1)]
    public void InvalidResizeCannotCorruptPersistedRatio(double ratio)
    {
        var layout = new TerminalLayout();
        var tab = layout.AddTab("Main");
        layout.Split(tab.Root.Id, SplitDirection.Right);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Resize(tab.Root.Id, ratio));
        Assert.Equal(0.5, tab.Root.Ratio);
    }

    /// <summary>
    /// 验证：布局中存在设置了分割轴却没有子节点的畸形节点时，<see cref="WorkspaceSettingsStore.Save"/> 抛出
    /// <see cref="InvalidDataException"/>，且在写盘前就拒绝，不会生成 <c>workspace.json</c>。
    /// </summary>
    [Fact]
    public void MalformedSplitIsRejectedBeforeSettingsAreWritten()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayout", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceSettingsStore(state);
            var settings = store.Load();
            var layout = new TerminalLayout();
            layout.AddTab("Broken").Root.Axis = SplitAxis.Horizontal;
            settings.ProjectLayouts["p"] = layout;
            Assert.Throws<InvalidDataException>(() => store.Save(settings));
            Assert.False(File.Exists(Path.Combine(state, "workspace.json")));
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    /// <summary>
    /// 验证：从手写的 <c>workspace.json</c> 读取含项目标签页的设置后原样保存，文件中仍保留 <c>ProjectLayouts</c>，
    /// 标签页名称 <c>Review</c> 与窗格绑定的会话 <c>session-a</c> 都没有丢失。
    /// </summary>
    [Fact]
    public void ProjectTabsAndPaneBindingsSurviveSettingsRoundTrip()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            var path = Path.Combine(state, "workspace.json");
            File.WriteAllText(path, """
                {"ProjectLayouts":{"project-a":{"ActiveTabId":"tab-a","Tabs":[
                    {"Id":"tab-a","Name":"Review","ActivePaneId":"pane-a","Root":{"Id":"pane-a","SessionId":"session-a"}}
                ]}}}
                """);
            var store = new WorkspaceSettingsStore(state);
            store.Save(store.Load());
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(saved.RootElement.TryGetProperty("ProjectLayouts", out var layouts), "Saving workspace settings discarded project tabs");
            var tab = layouts.GetProperty("project-a").GetProperty("Tabs")[0];
            Assert.Equal("Review", tab.GetProperty("Name").GetString());
            Assert.Equal("session-a", tab.GetProperty("Root").GetProperty("SessionId").GetString());
        }
        finally { Directory.Delete(state, true); }
    }
}
