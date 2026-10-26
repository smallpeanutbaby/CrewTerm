using CrewTerm.Core.Layouts;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 显示一个项目的终端标签页，以及按分割树动态创建的终端面板与分隔条。
/// </summary>
/// <remarks>
/// 视图只负责把 <see cref="TerminalLayout"/> 模型映射为界面：顶部一行是 herdr 风格的标签栏（各标签名、紧随其后的 <c>+</c> 新建标签，
/// 分屏时右侧以暗色显示面板数；右键标签栏打开命令菜单），
/// 其下是面板区域。每个分割树叶子对应一个 <see cref="FrameView"/> 包裹的 <see cref="TerminalPane"/>，
/// 只为当前标签页创建；切换标签页或修改分割树后调用 <see cref="Rebuild"/> 同步。
/// 面板与会话的绑定先保存在视图上，需要持久化时由 <see cref="SaveBindings"/> 写回模型。
/// 创建面板不会启动任何进程。所有成员都应在 UI 线程使用。
/// </remarks>
public sealed class TerminalLayoutView : View
{
    /// <summary>
    /// 活动标签的前景色（粗体）。
    /// </summary>
    private const int ActiveTabColor = 0xffffff;

    /// <summary>
    /// 活动标签的背景色。
    /// </summary>
    private const int ActiveTabBackground = 0x25425c;

    /// <summary>
    /// 其他标签的前景色。
    /// </summary>
    private const int TabColor = 0x9399b2;

    /// <summary>
    /// 新建标签按钮 <c>+</c> 的颜色。
    /// </summary>
    private const int PlusColor = 0x89b4fa;

    /// <summary>
    /// 面板数提示的暗色。
    /// </summary>
    private const int CountColor = 0x6c7086;

    /// <summary>
    /// 标签栏背景色，与工作台窗口一致。
    /// </summary>
    private const int BarBackground = 0x10151d;

    /// <summary>
    /// 标签栏右侧为面板数提示保留的列数。
    /// </summary>
    private const int CountWidth = 9;

    /// <summary>
    /// 顶部一行高的标签栏，子视图依次为各标签、<c>+</c> 与面板数提示。
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
    /// 点击标签栏上的 <c>+</c> 时调用，由工作台询问新标签名并创建标签页。
    /// </summary>
    public Action? NewTabRequested { get; set; }

    /// <summary>
    /// 右键单击标签栏时调用，由工作台打开命令面板。
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
        tabBar.SetScheme(new Scheme { Normal = new Attribute(new Color(TabColor), new Color(BarBackground)) });
        paneArea.SubViewLayout += (_, _) => ArrangePanes();
        tabBar.SubViewLayout += (_, _) => ArrangeTabs();
        tabBar.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.RightButtonPressed) && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
            { mouse.Handled = true; MenuRequested?.Invoke(); }
        };
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
    /// 重新创建标签栏的全部子视图：每个标签一个可点击的标签名（活动标签高亮），随后是 <c>+</c> 与面板数提示，然后排列位置。
    /// </summary>
    /// <remarks>
    /// 子视图的顺序是 <see cref="ArrangeTabs"/> 的前提：前 N 个对应 N 个标签，最后两个依次为 <c>+</c> 与面板数提示。
    /// 面板数只在当前标签页分屏时显示，例如 <c>3 panes</c>。
    /// </remarks>
    private void BuildTabs()
    {
        foreach (var child in tabBar.SubViews.ToArray()) { tabBar.Remove(child); child.Dispose(); }
        foreach (var tab in layout.Tabs)
        {
            var active = tab.Id == layout.ActiveTabId;
            var button = TabAction(" " + tab.Name + " ", () => SelectTab(tab.Id),
                new Attribute(new Color(active ? ActiveTabColor : TabColor), new Color(active ? ActiveTabBackground : BarBackground), active ? TextStyle.Bold : TextStyle.None));
            button.Data = tab.Id;
            tabBar.Add(button);
        }
        var plus = TabAction(" + ", () => NewTabRequested?.Invoke(), new Attribute(new Color(PlusColor), new Color(BarBackground)));
        var count = new Label { Text = bindings.Count > 1 ? $"{bindings.Count} panes" : "", Height = 1, Data = "count" };
        count.SetScheme(new Scheme { Normal = new Attribute(new Color(CountColor), new Color(BarBackground)) });
        tabBar.Add(plus, count);
        ArrangeTabs();
    }

    /// <summary>
    /// 创建一个不可获得焦点、单行高的标签，左键按下（不含拖动）时执行操作。
    /// </summary>
    /// <param name="text">显示文本。</param>
    /// <param name="action">左键按下时在 UI 线程执行的操作。</param>
    /// <param name="attribute">显示颜色与样式。</param>
    /// <returns>新建的标签视图，尚未加入任何父视图。</returns>
    private static Label TabAction(string text, Action action, Attribute attribute)
    {
        var label = new Label { Text = text, Height = 1, CanFocus = false };
        label.SetScheme(new Scheme { Normal = attribute });
        label.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
            { mouse.Handled = true; action(); }
        };
        return label;
    }

    /// <summary>
    /// 按标签栏当前宽度依次排列标签、<c>+</c> 与面板数提示。
    /// </summary>
    /// <remarks>
    /// 每个标签按名称宽度占 4–24 列，从左到右紧密排列，<c>+</c> 紧跟在最后一个可见标签之后，面板数提示靠右。
    /// 放不下时从第一个标签开始显示尽可能多的标签；活动标签超出这一段时改为显示以活动标签结尾的一段连续标签，其余标签隐藏。
    /// 标签栏尚无子视图时什么也不做；子视图顺序必须与 <see cref="BuildTabs"/> 创建的一致。
    /// </remarks>
    private void ArrangeTabs()
    {
        if (tabBar.SubViews.Count < layout.Tabs.Count + 2) return;
        var width = Math.Max(0, tabBar.Viewport.Width);
        var views = tabBar.SubViews.ToArray();
        var widths = layout.Tabs.Select((_, i) => Math.Clamp(views[i].Text.GetColumns(), 4, 24)).ToArray();
        var room = Math.Max(0, width - 3 - CountWidth);
        var active = Math.Max(0, layout.Tabs.FindIndex(t => t.Id == layout.ActiveTabId));
        // 先从第一个标签开始尽量放；放不下活动标签时，改为以活动标签结尾向前放。
        var start = 0;
        while (start < active && widths.Skip(start).Take(active - start + 1).Sum() > room) start++;
        var x = 0;
        for (var i = 0; i < layout.Tabs.Count; i++)
        {
            var view = views[i];
            view.Visible = i >= start && x + widths[i] <= Math.Max(room, widths[start]);
            view.X = x; view.Y = 0; view.Width = widths[i];
            if (view.Visible) x += widths[i];
        }
        var plus = views[layout.Tabs.Count];
        plus.X = x; plus.Y = 0; plus.Width = 3;
        var count = views[layout.Tabs.Count + 1];
        count.X = Math.Max(x + 3, width - CountWidth); count.Y = 0; count.Width = CountWidth;
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
