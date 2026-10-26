using CrewTerm.Core.Layouts;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 显示一个项目的终端标签页，以及按分割树动态创建的终端面板与分隔条。
/// </summary>
/// <remarks>
/// 视图只负责把 <see cref="TerminalLayout"/> 模型映射为界面：顶部一行是标签栏（标签、<c>[+]</c> 新建、<c>[≡]</c> 菜单、面板计数），
/// 其下是面板区域。每个分割树叶子对应一个 <see cref="FrameView"/> 包裹的 <see cref="TerminalPane"/>，
/// 只为当前标签页创建；切换标签页或修改分割树后调用 <see cref="Rebuild"/> 同步。
/// 面板与会话的绑定先保存在视图上，需要持久化时由 <see cref="SaveBindings"/> 写回模型。
/// 创建面板不会启动任何进程。所有成员都应在 UI 线程使用。
/// </remarks>
public sealed class TerminalLayoutView : View
{
    /// <summary>
    /// 顶部一行高的标签栏，子视图依次为各标签、<c>[+]</c>、<c>[≡]</c> 与面板计数标签。
    /// </summary>
    private readonly View tabBar = new() { Width = Dim.Fill(), Height = 1 };

    /// <summary>
    /// 标签栏下方容纳终端面板框与分隔条的区域。
    /// </summary>
    private readonly View paneArea = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };

    /// <summary>
    /// 当前标签页中每个叶子节点标识（区分大小写）对应的面板框与终端面板。
    /// </summary>
    private readonly Dictionary<string, (FrameView Frame, TerminalPane Pane)> bindings = [];

    /// <summary>
    /// 当前标签页的分隔条视图，<see cref="View.Id"/> 为对应分割节点的标识；每次重建时全部重新创建。
    /// </summary>
    private readonly List<View> dividers = [];

    /// <summary>
    /// 正在显示的项目布局模型；由 <see cref="ShowLayout"/> 替换。
    /// </summary>
    private TerminalLayout layout = new();

    /// <summary>
    /// 最近一次重建所显示的标签页标识；尚未重建过时为 <see langword="null"/>，此时 <see cref="ArrangePanes"/> 不做任何事。
    /// </summary>
    private string? displayedTab;

    /// <summary>
    /// 是否正在执行 <see cref="Rebuild"/>；期间忽略由移除视图引起的焦点变化回调。
    /// </summary>
    private bool rebuilding;

    /// <summary>
    /// 当前显示、可持久化的项目布局模型（与传入 <see cref="ShowLayout"/> 的是同一对象）。
    /// </summary>
    /// <remarks>
    /// 调用方直接修改模型（分割、关闭、新建标签等）之后必须调用 <see cref="Rebuild"/>。
    /// </remarks>
    public TerminalLayout LayoutModel => layout;

    /// <summary>
    /// 当前标签页中的全部面板，按分割树的视觉顺序（从左到右、从上到下）排列；每次读取都会生成新数组。
    /// </summary>
    public IReadOnlyList<TerminalPane> Panes => layout.ActiveTab.Panes.Select(n => bindings[n.Id].Pane).ToArray();

    /// <summary>
    /// 当前标签页中获得焦点或最近一次选中的面板；模型未记录活动面板时取第一个面板。
    /// </summary>
    public TerminalPane ActivePane => bindings[layout.ActiveTab.ActivePaneId ?? layout.ActiveTab.Panes.First().Id].Pane;

    /// <summary>
    /// 新建终端面板后调用，供工作台为其连接输入、复制与右键菜单回调。
    /// </summary>
    public Action<TerminalPane>? PaneCreated { get; set; }

    /// <summary>
    /// 活动标签页或获得焦点的面板改变后调用（模型中的选择已更新）。
    /// </summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>
    /// 点击标签栏上的 <c>[+]</c> 时调用，由工作台询问新标签名并创建标签页。
    /// </summary>
    public Action? NewTabRequested { get; set; }

    /// <summary>
    /// 点击标签栏上的 <c>[≡]</c> 时调用，由工作台打开命令面板。
    /// </summary>
    public Action? MenuRequested { get; set; }

    /// <summary>
    /// 创建填满父视图的布局视图，初始模型为一个没有标签页的空布局，调用 <see cref="ShowLayout"/> 后才显示面板。
    /// </summary>
    /// <remarks>
    /// 面板区域与标签栏每次布局子视图时都会按最新尺寸重新排列面板与标签，使窗口缩放后几何位置保持正确。
    /// </remarks>
    public TerminalLayoutView()
    {
        Width = Dim.Fill(); Height = Dim.Fill(); CanFocus = true;
        Add(tabBar, paneArea);
        paneArea.SubViewLayout += (_, _) => ArrangePanes();
        tabBar.SubViewLayout += (_, _) => ArrangeTabs();
    }

    /// <summary>
    /// 显示一个布局并创建缺少的面板，不会启动任何进程。
    /// </summary>
    /// <remarks>
    /// 布局没有任何标签页时会先在模型中添加一个名为 <c>Terminal</c> 的空标签页（直接修改传入的对象）。
    /// 旧布局中不再使用的面板视图会在重建时被释放。
    /// </remarks>
    /// <param name="value">要显示的项目布局；之后通过 <see cref="LayoutModel"/> 访问的就是这个对象。</param>
    public void ShowLayout(TerminalLayout value)
    {
        layout = value;
        if (layout.Tabs.Count == 0) layout.AddTab("Terminal");
        Rebuild();
    }

    /// <summary>
    /// 在标签页或分割树被修改后刷新视图。
    /// </summary>
    /// <remarks>
    /// 重建期间设置 <see cref="rebuilding"/>，使移除视图引起的焦点转移不会被误当成用户选择了面板。
    /// </remarks>
    public void Rebuild()
    {
        rebuilding = true;
        try { RebuildViews(); }
        finally { rebuilding = false; }
    }

    /// <summary>
    /// 按模型的活动标签页同步面板、分隔条与标签栏视图。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>确保模型记录了活动标签页与活动面板（缺省时取第一个面板）。</description></item>
    /// <item><description>释放不再属于当前标签页的面板框；为新叶子创建面板框，初始绑定模型中保存的会话，并通过 <see cref="PaneCreated"/> 通知工作台。</description></item>
    /// <item><description>新面板获得焦点时在模型中选中它并触发 <see cref="SelectionChanged"/>；来自旧布局或旧标签页的过期回调被忽略。</description></item>
    /// <item><description>分隔条全部重新创建：拖动时把新比例写入模型并立即重新排列面板。</description></item>
    /// </list>
    /// 已存在的面板保留原有视图，因此其会话绑定、选区与滚动位置不受影响。
    /// </remarks>
    private void RebuildViews()
    {
        var owner = layout;
        var tab = layout.ActiveTab;
        layout.ActiveTabId = tab.Id;
        tab.ActivePaneId ??= tab.Panes.First().Id;
        var ids = tab.Panes.Select(n => n.Id).ToHashSet();
        foreach (var id in bindings.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var binding = bindings[id]; paneArea.Remove(binding.Frame); binding.Frame.Dispose(); bindings.Remove(id);
        }
        foreach (var node in tab.Panes)
        {
            if (!bindings.TryGetValue(node.Id, out var binding))
            {
                var pane = new TerminalPane { SessionId = node.SessionId };
                var frame = new FrameView { Title = "TERMINAL  ·  F3 New" };
                frame.Add(pane); paneArea.Add(frame);
                bindings[node.Id] = (frame, pane);
                var id = node.Id;
                pane.HasFocusChanged += (_, _) =>
                {
                    // 移除拥有焦点的视图时，Terminal.Gui 会把焦点交给另一个旧的子视图。
                    // 这类回调来自正在被替换的布局（或已切走的标签页），不能用它们在新布局中选择面板。
                    if (rebuilding || !pane.HasFocus || !ReferenceEquals(owner, layout) ||
                        layout.ActiveTab.Id != tab.Id || !bindings.TryGetValue(id, out var current) || !ReferenceEquals(current.Pane, pane)) return;
                    layout.SelectPane(id); SelectionChanged?.Invoke();
                };
                PaneCreated?.Invoke(pane);
            }
        }
        foreach (var divider in dividers) { paneArea.Remove(divider); divider.Dispose(); }
        dividers.Clear();
        foreach (var place in LayoutGeometry.Measure(tab.Root, paneArea.Viewport.Width, paneArea.Viewport.Height).Where(p => p.IsDivider))
        {
            var divider = new PaneDivider(place, ratio =>
            {
                layout.Resize(place.NodeId, ratio); ArrangePanes(); SetNeedsLayout();
            }) { Id = place.NodeId };
            dividers.Add(divider); paneArea.Add(divider);
        }
        displayedTab = tab.Id;
        BuildTabs(); ArrangePanes(); SetNeedsLayout();
    }

    /// <summary>
    /// 把当前标签页各面板绑定的会话标识写回可序列化的布局模型。
    /// </summary>
    /// <remarks>
    /// 只写当前标签页；其他标签页的绑定在离开时已经写回。只修改内存中的模型，不写盘。
    /// </remarks>
    public void SaveBindings()
    {
        foreach (var node in layout.ActiveTab.Panes)
            if (bindings.TryGetValue(node.Id, out var binding)) node.SessionId = binding.Pane.SessionId;
    }

    /// <summary>
    /// 更新当前显示的某个终端面板框的标题；面板不属于当前标签页时什么也不做。
    /// </summary>
    /// <param name="pane">要更新标题的面板（按引用匹配）。</param>
    /// <param name="title">新标题。</param>
    public void SetTitle(TerminalPane pane, string title)
    {
        foreach (var binding in bindings.Values)
            if (ReferenceEquals(binding.Pane, pane)) { binding.Frame.Title = title; break; }
    }

    /// <summary>
    /// 选择一个已有的标签页：先保存当前标签页的会话绑定，再重建视图、聚焦该标签页的活动面板并触发 <see cref="SelectionChanged"/>。
    /// </summary>
    /// <param name="id">标签页标识；不存在时什么也不做。</param>
    public void SelectTab(string id)
    {
        if (!layout.Tabs.Any(t => t.Id == id)) return;
        SaveBindings(); layout.ActiveTabId = id; Rebuild();
        ActivePane.SetFocus(); SelectionChanged?.Invoke();
    }

    /// <summary>
    /// 重新创建标签栏的全部子视图：每个标签一个可点击标签（活动标签前加 <c>●</c>），随后是 <c>[+]</c>、<c>[≡]</c> 与面板计数，然后排列位置。
    /// </summary>
    /// <remarks>
    /// 子视图的顺序是 <see cref="ArrangeTabs"/> 的前提：前 N 个对应 N 个标签，最后三个依次为控制按钮与计数。
    /// </remarks>
    private void BuildTabs()
    {
        foreach (var child in tabBar.SubViews.ToArray()) { tabBar.Remove(child); child.Dispose(); }
        foreach (var tab in layout.Tabs)
        {
            var button = TabAction((tab.Id == layout.ActiveTabId ? "● " : "") + tab.Name, () => SelectTab(tab.Id));
            button.Data = tab.Id;
            tabBar.Add(button);
        }
        var plus = TabAction("[+]", () => NewTabRequested?.Invoke());
        var menu = TabAction("[≡]", () => MenuRequested?.Invoke());
        var count = new Label { Text = $"{bindings.Count} pane{(bindings.Count == 1 ? "" : "s")}", Height = 1, Data = "count" };
        tabBar.Add(plus, menu, count);
        ArrangeTabs();
    }

    /// <summary>
    /// 创建一个不可获得焦点、单行高的标签，左键按下（不含拖动）时执行操作。
    /// </summary>
    /// <param name="text">显示文本。</param>
    /// <param name="action">左键按下时在 UI 线程执行的操作。</param>
    /// <returns>新建的标签视图，尚未加入任何父视图。</returns>
    private static Label TabAction(string text, Action action)
    {
        var label = new Label { Text = text, Height = 1, CanFocus = false };
        label.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
            { mouse.Handled = true; action(); }
        };
        return label;
    }

    /// <summary>
    /// 按标签栏当前宽度排列标签与右侧控件。
    /// </summary>
    /// <remarks>
    /// 右侧固定保留 17 列给 <c>[+]</c>（距右边 16 列）、<c>[≡]</c>（距右边 12 列）与 8 列宽的面板计数；
    /// 其余宽度平分给各标签，每个标签 4–24 列。放不下时从第一个标签开始显示尽可能多的标签；
    /// 活动标签超出这一段时改为显示以活动标签结尾的一段连续标签，其余标签隐藏。
    /// 标签栏尚无子视图时什么也不做；子视图顺序必须与 <see cref="BuildTabs"/> 创建的一致。
    /// </remarks>
    private void ArrangeTabs()
    {
        if (tabBar.SubViews.Count == 0) return;
        var width = Math.Max(0, tabBar.Viewport.Width);
        var tabWidth = Math.Clamp((width - 17) / Math.Max(1, layout.Tabs.Count), 4, 24);
        var active = Math.Max(0, layout.Tabs.FindIndex(t => t.Id == layout.ActiveTabId));
        var capacity = Math.Max(1, (width - 17) / tabWidth);
        var start = Math.Max(0, active - capacity + 1);
        var x = 0;
        for (var i = 0; i < layout.Tabs.Count; i++)
        {
            var view = tabBar.SubViews.ElementAt(i);
            view.Visible = i >= start && i < start + capacity;
            view.X = x; view.Y = 0; view.Width = tabWidth;
            if (view.Visible) x += tabWidth;
        }
        var controls = tabBar.SubViews.Skip(layout.Tabs.Count).ToArray();
        controls[0].X = Math.Max(0, width - 16); controls[0].Width = 3;
        controls[1].X = Math.Max(0, width - 12); controls[1].Width = 3;
        controls[2].X = Math.Max(0, width - 8); controls[2].Width = 8;
    }

    /// <summary>
    /// 按面板区域当前尺寸重新计算活动标签页的几何布局，并设置各面板框与分隔条的位置和大小。
    /// </summary>
    /// <remarks>
    /// 宽或高为 0 的区域对应的视图被隐藏。分隔条按方向填充 <c>│</c> 或 <c>─</c>，并更新其 <see cref="PaneDivider.Placement"/>，
    /// 使拖动时按最新的父区域换算比例。尚未重建过或没有标签页时什么也不做。
    /// </remarks>
    private void ArrangePanes()
    {
        if (displayedTab is null || layout.Tabs.Count == 0) return;
        foreach (var place in LayoutGeometry.Measure(layout.ActiveTab.Root, Math.Max(0, paneArea.Viewport.Width), Math.Max(0, paneArea.Viewport.Height)))
        {
            var view = place.IsDivider ? dividers.FirstOrDefault(d => d.Id == place.NodeId) : bindings.GetValueOrDefault(place.NodeId).Frame;
            if (view is null) continue;
            view.X = place.Bounds.X; view.Y = place.Bounds.Y;
            view.Width = place.Bounds.Width; view.Height = place.Bounds.Height;
            view.Visible = place.Bounds.Width > 0 && place.Bounds.Height > 0;
            if (view is Label label) label.Text = place.Axis == SplitAxis.Horizontal
                ? string.Join('\n', Enumerable.Repeat("│", place.Bounds.Height)) : new string('─', place.Bounds.Width);
            if (view is PaneDivider divider) divider.Placement = place;
        }
    }
}
