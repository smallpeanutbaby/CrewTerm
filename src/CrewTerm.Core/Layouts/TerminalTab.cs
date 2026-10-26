using System.Text.Json.Serialization;

namespace CrewTerm.Core.Layouts;

/// <summary>
/// 项目中一个有名称的终端标签页，持有一棵分屏树。
/// </summary>
public sealed class TerminalTab
{
    /// <summary>
    /// 稳定的标签页标识（GUID 的前 12 位十六进制字符）；与布局中所有节点的标识互不重复。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>
    /// 显示给用户的标签页标题，1–60 个可打印字符，默认为 <c>Terminal</c>。
    /// </summary>
    public string Name { get; set; } = "Terminal";
    /// <summary>
    /// 分屏树的根；标签页只有一个窗格时，根就是这个窗格。
    /// </summary>
    public LayoutNode Root { get; set; } = new();
    /// <summary>
    /// 选中该标签页时获得焦点的窗格标识；为 <see langword="null"/> 表示未指定。
    /// </summary>
    public string? ActivePaneId { get; set; }
    /// <summary>
    /// 按视觉顺序（从左到右、从上到下）排列的窗格节点；每次枚举都重新遍历树。不参与序列化。
    /// </summary>
    [JsonIgnore] public IEnumerable<LayoutNode> Panes => TerminalLayout.Leaves(Root);
}
