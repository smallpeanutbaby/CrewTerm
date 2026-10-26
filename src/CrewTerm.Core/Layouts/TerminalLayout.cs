using System.Text.Json.Serialization;
using CrewTerm.Core.Settings;

namespace CrewTerm.Core.Layouts;

/// <summary>
/// 一个项目的全部终端标签页（可序列化），按项目保存在 <see cref="WorkspaceSettings.ProjectLayouts"/> 中。
/// </summary>
/// <remarks>
/// 每个标签页持有一棵分屏树（<see cref="LayoutNode"/>）。这里的操作只修改布局：关闭窗格或标签页不会结束
/// 其绑定的终端进程，是否结束由调用方决定。上限：每个项目最多 16 个标签页、每个标签页最多 16 个窗格、
/// 树深度不超过 16；标签页名称为 1–60 个可打印字符。本类不是线程安全的，应只在 UI 线程上使用。
/// </remarks>
public sealed partial class TerminalLayout
{
    /// <summary>
    /// 按显示顺序排列的标签页。
    /// </summary>
    public List<TerminalTab> Tabs { get; set; } = [];
    /// <summary>
    /// 当前选中标签页的标识；为 <see langword="null"/> 或指向不存在的标签页时，<see cref="ActiveTab"/> 退回第一个标签页。
    /// </summary>
    public string? ActiveTabId { get; set; }
    /// <summary>
    /// 当前选中的标签页：按 <see cref="ActiveTabId"/> 查找，找不到时为第一个标签页。不参与序列化。
    /// </summary>
    /// <exception cref="InvalidOperationException">布局中还没有任何标签页。</exception>
    [JsonIgnore] public TerminalTab ActiveTab => Tabs.FirstOrDefault(t => t.Id == ActiveTabId)
        ?? Tabs.FirstOrDefault() ?? throw new InvalidOperationException("Create a terminal tab first");
    /// <summary>
    /// 新建一个只含一个窗格的标签页，追加到末尾并选中；该窗格成为新标签页的活动窗格。
    /// </summary>
    /// <param name="name">标签页名称，保存时去掉首尾空白；必须为 1–60 个可打印字符。</param>
    /// <param name="sessionId">新窗格绑定的 daemon 会话标识；为 <see langword="null"/> 时是空窗格。</param>
    /// <returns>新建的标签页。</returns>
    /// <exception cref="ArgumentException">名称为空白、过长或包含控制字符。</exception>
    /// <exception cref="InvalidOperationException">该项目已有 16 个标签页。</exception>
    public TerminalTab AddTab(string name, string? sessionId = null)
    {
        ValidateName(name);
        if (Tabs.Count >= 16) throw new InvalidOperationException("Maximum 16 tabs per project");
        var tab = new TerminalTab { Name = name.Trim(), Root = new() { SessionId = sessionId } };
        tab.ActivePaneId = tab.Root.Id;
        Tabs.Add(tab); ActiveTabId = tab.Id;
        return tab;
    }
    /// <summary>
    /// 把指定窗格一分为二，选中其所在标签页并聚焦新窗格。
    /// </summary>
    /// <remarks>
    /// 原窗格在树中的位置被一个新的分割节点取代：新窗格位于 <paramref name="direction"/> 指定的一侧，
    /// 原窗格（连同其会话绑定）位于另一侧，两者各占一半（<see cref="LayoutNode.Ratio"/> 为 0.5）。
    /// </remarks>
    /// <param name="paneId">要分割的窗格标识，可以属于任意标签页。</param>
    /// <param name="direction">新窗格相对原窗格的位置。</param>
    /// <param name="sessionId">新窗格绑定的 daemon 会话标识；为 <see langword="null"/> 时是空窗格。</param>
    /// <returns>新窗格节点。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> 不是已定义的枚举值。</exception>
    /// <exception cref="KeyNotFoundException">任何标签页中都没有该窗格。</exception>
    /// <exception cref="InvalidOperationException">该标签页已有 16 个窗格。</exception>
    public LayoutNode Split(string paneId, SplitDirection direction, string? sessionId = null)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        var tab = FindTabForPane(paneId);
        if (tab.Panes.Count() >= 16) throw new InvalidOperationException("Maximum 16 panes per tab");
        var original = tab.Panes.Single(p => p.Id == paneId);
        var added = new LayoutNode { SessionId = sessionId };
        var before = direction is SplitDirection.Left or SplitDirection.Up;
        var branch = new LayoutNode
        {
            Axis = direction is SplitDirection.Left or SplitDirection.Right ? SplitAxis.Horizontal : SplitAxis.Vertical,
            First = before ? added : original, Second = before ? original : added
        };
        tab.Root = Replace(tab.Root, paneId, branch);
        ActiveTabId = tab.Id; tab.ActivePaneId = added.Id;
        return added;
    }
    /// <summary>
    /// 从布局中移除一个窗格，不结束其绑定的进程。
    /// </summary>
    /// <remarks>
    /// 窗格是标签页中唯一的窗格（即树根）时，改为关闭整个标签页（见 <see cref="CloseTab"/>）；
    /// 否则它的兄弟子树顶替父分割节点的位置。被移除的是活动窗格时，活动窗格改为该标签页中的第一个窗格。
    /// </remarks>
    /// <param name="paneId">窗格标识。</param>
    /// <exception cref="KeyNotFoundException">任何标签页中都没有该窗格。</exception>
    public void ClosePane(string paneId)
    {
        var tab = FindTabForPane(paneId);
        if (tab.Root.Id == paneId) { CloseTab(tab.Id); return; }
        tab.Root = Remove(tab.Root, paneId)!;
        if (tab.ActivePaneId == paneId) tab.ActivePaneId = tab.Panes.First().Id;
    }
    /// <summary>
    /// 移除一个标签页，不结束其中任何窗格绑定的进程。
    /// </summary>
    /// <remarks>
    /// 移除后若一个标签页都不剩，自动新建并选中一个名为 <c>Terminal</c> 的空标签页，保证布局始终至少有一个标签页；
    /// 若移除的是选中的标签页，改为选中原位置上的下一个标签页（它是最后一个时选中新的最后一个）。
    /// </remarks>
    /// <param name="tabId">标签页标识。</param>
    /// <exception cref="KeyNotFoundException">没有该标签页。</exception>
    public void CloseTab(string tabId)
    {
        var index = Tabs.FindIndex(t => t.Id == tabId);
        if (index < 0) throw new KeyNotFoundException("Tab not found");
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0) AddTab("Terminal");
        else if (ActiveTabId == tabId) ActiveTabId = Tabs[Math.Min(index, Tabs.Count - 1)].Id;
    }
    /// <summary>
    /// 重命名标签页。
    /// </summary>
    /// <param name="tabId">标签页标识。</param>
    /// <param name="name">新名称，保存时去掉首尾空白；必须为 1–60 个可打印字符。</param>
    /// <exception cref="ArgumentException">名称为空白、过长或包含控制字符。</exception>
    /// <exception cref="KeyNotFoundException">没有该标签页。</exception>
    public void RenameTab(string tabId, string name)
    {
        ValidateName(name);
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? throw new KeyNotFoundException("Tab not found");
        tab.Name = name.Trim();
    }
    /// <summary>
    /// 选中窗格所在的标签页，并把该窗格设为这个标签页的活动窗格。
    /// </summary>
    /// <param name="paneId">窗格标识，可以属于任意标签页。</param>
    /// <exception cref="KeyNotFoundException">任何标签页中都没有该窗格。</exception>
    public void SelectPane(string paneId)
    {
        var tab = FindTabForPane(paneId);
        ActiveTabId = tab.Id; tab.ActivePaneId = paneId;
    }
    /// <summary>
    /// 修改分割节点（分隔线）的比例。在所有标签页中查找该节点。
    /// </summary>
    /// <param name="splitId">分割节点标识；窗格标识不算数。</param>
    /// <param name="ratio">分配给第一个子区域的比例，必须是 0.1–0.9 之间的有限数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratio"/> 不是有限数或超出范围。</exception>
    /// <exception cref="KeyNotFoundException">没有该分割节点。</exception>
    public void Resize(string splitId, double ratio)
    {
        if (!double.IsFinite(ratio) || ratio is < 0.1 or > 0.9) throw new ArgumentOutOfRangeException(nameof(ratio));
        var node = Tabs.SelectMany(t => Nodes(t.Root)).FirstOrDefault(n => n.Id == splitId && !n.IsPane)
            ?? throw new KeyNotFoundException("Divider not found");
        node.Ratio = ratio;
    }

    /// <summary>
    /// 校验从磁盘加载的布局，拒绝格式错误、超出上限或有歧义的布局树。
    /// </summary>
    /// <remarks>
    /// 要求：标签页列表非空引用且不超过 16 个；每个标签页的标识非空白、名称为 1–60 个非控制字符；
    /// 标签页与所有节点的标识在整个布局内互不重复；每棵树都符合 <see cref="ValidateNode"/> 的规则且窗格不超过 16 个；
    /// 非空的 <see cref="TerminalTab.ActivePaneId"/> 必须是本标签页的窗格，非空的 <see cref="ActiveTabId"/> 必须存在。
    /// 由 <see cref="WorkspaceSettingsStore"/> 在加载与保存时调用。
    /// </remarks>
    /// <exception cref="InvalidDataException">布局不符合上述任何一条规则。</exception>
    public void Validate()
    {
        if (Tabs is null || Tabs.Count > 16) throw new InvalidDataException("Invalid terminal tabs");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in Tabs)
        {
            if (tab is null || string.IsNullOrWhiteSpace(tab.Id) || !identities.Add(tab.Id) ||
                string.IsNullOrWhiteSpace(tab.Name) || tab.Name.Length > 60 || tab.Name.Any(char.IsControl))
                throw new InvalidDataException("Invalid terminal tab");
            var count = 0;
            ValidateNode(tab.Root, identities, ref count, 0);
            if (count > 16) throw new InvalidDataException("Maximum 16 panes per tab");
            if (tab.ActivePaneId is not null && !tab.Panes.Any(p => p.Id == tab.ActivePaneId))
                throw new InvalidDataException("Active pane does not belong to its tab");
        }
        if (ActiveTabId is not null && !Tabs.Any(t => t.Id == ActiveTabId))
            throw new InvalidDataException("Active tab does not exist");
    }

    /// <summary>
    /// 递归校验一棵分屏子树，并统计其中的窗格数。
    /// </summary>
    /// <remarks>
    /// 每个节点都必须存在、深度不超过 16、标识非空白且未出现过；窗格不能有子节点；
    /// 分割节点的方向必须是已定义的枚举值、比例为 0.1–0.9 之间的有限数、不能绑定会话，且两个子节点都必须存在。
    /// </remarks>
    /// <param name="node">要校验的节点；为 <see langword="null"/> 时视为格式错误。</param>
    /// <param name="ids">已出现过的标识（含标签页标识），本方法会把新节点的标识加入其中。</param>
    /// <param name="count">累加的窗格数。</param>
    /// <param name="depth">当前节点的深度，根为 0。</param>
    /// <exception cref="InvalidDataException">子树不符合上述规则。</exception>
    private static void ValidateNode(LayoutNode? node, HashSet<string> ids, ref int count, int depth)
    {
        if (node is null || depth > 16 || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
            throw new InvalidDataException("Malformed terminal layout tree");
        if (node.IsPane)
        {
            if (node.First is not null || node.Second is not null) throw new InvalidDataException("Pane cannot contain children");
            count++;
            return;
        }
        if (!Enum.IsDefined(node.Axis!.Value) || !double.IsFinite(node.Ratio) || node.Ratio is < 0.1 or > 0.9 || node.SessionId is not null)
            throw new InvalidDataException("Invalid terminal divider");
        ValidateNode(node.First, ids, ref count, depth + 1);
        ValidateNode(node.Second, ids, ref count, depth + 1);
    }

    /// <summary>
    /// 按视觉顺序（从左到右、从上到下）列出树中的窗格节点。
    /// </summary>
    /// <param name="root">树根。</param>
    /// <returns>延迟枚举的窗格序列，每次枚举都重新遍历树。</returns>
    internal static IEnumerable<LayoutNode> Leaves(LayoutNode root) => Nodes(root).Where(n => n.IsPane);
    /// <summary>
    /// 前序遍历树中的全部节点：先节点本身，再 <see cref="LayoutNode.First"/> 子树，最后 <see cref="LayoutNode.Second"/> 子树。
    /// </summary>
    /// <param name="root">树根。</param>
    /// <returns>延迟枚举的节点序列。</returns>
    private static IEnumerable<LayoutNode> Nodes(LayoutNode root)
    {
        yield return root;
        if (root.First is not null) foreach (var child in Nodes(root.First)) yield return child;
        if (root.Second is not null) foreach (var child in Nodes(root.Second)) yield return child;
    }
    /// <summary>
    /// 查找包含指定窗格的标签页。
    /// </summary>
    /// <param name="paneId">窗格标识。</param>
    /// <returns>第一个包含该窗格的标签页。</returns>
    /// <exception cref="KeyNotFoundException">任何标签页中都没有该窗格。</exception>
    private TerminalTab FindTabForPane(string paneId) => Tabs.FirstOrDefault(t => t.Panes.Any(p => p.Id == paneId))
        ?? throw new KeyNotFoundException("Pane not found");
    /// <summary>
    /// 在树中把指定标识的节点替换为另一个节点，原地修改父节点的子节点引用。
    /// </summary>
    /// <param name="root">树根。</param>
    /// <param name="id">被替换节点的标识。</param>
    /// <param name="replacement">替换进来的节点；不会再在它内部继续查找。</param>
    /// <returns>替换后的树根：根本身被替换时为 <paramref name="replacement"/>，否则为 <paramref name="root"/>。</returns>
    private static LayoutNode Replace(LayoutNode root, string id, LayoutNode replacement)
    {
        if (root.Id == id) return replacement;
        if (root.First is not null) root.First = Replace(root.First, id, replacement);
        if (root.Second is not null) root.Second = Replace(root.Second, id, replacement);
        return root;
    }
    /// <summary>
    /// 从树中删除指定标识的节点；失去一个子节点的分割节点随之消失，由剩下的兄弟子树顶替它的位置。
    /// </summary>
    /// <param name="root">子树根；分割节点必须有两个子节点。</param>
    /// <param name="id">要删除的节点标识。</param>
    /// <returns>删除后的子树根；整棵子树就是被删节点时为 <see langword="null"/>。</returns>
    private static LayoutNode? Remove(LayoutNode root, string id)
    {
        if (root.Id == id) return null;
        if (root.IsPane) return root;
        root.First = Remove(root.First!, id);
        root.Second = Remove(root.Second!, id);
        return root.First is null ? root.Second : root.Second is null ? root.First : root;
    }
    /// <summary>
    /// 校验标签页名称：去掉首尾空白后为 1–60 个字符，且不含控制字符。
    /// </summary>
    /// <param name="name">名称。</param>
    /// <exception cref="ArgumentException">名称不符合要求。</exception>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60 || name.Any(char.IsControl))
            throw new ArgumentException("Tab name must be 1–60 printable characters", nameof(name));
    }
}
