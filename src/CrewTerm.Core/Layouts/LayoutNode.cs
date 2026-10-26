using System.Text.Json.Serialization;

namespace CrewTerm.Core.Layouts;

/// <summary>
/// 分屏树中的一个节点：要么是显示终端的窗格（叶子），要么是把所在区域一分为二的分割节点。
/// </summary>
/// <remarks>
/// <see cref="Axis"/> 为 <see langword="null"/> 表示窗格，此时没有子节点；否则是分割节点，
/// <see cref="First"/> 与 <see cref="Second"/> 都必须存在且不能绑定会话（见 <see cref="TerminalLayout.Validate"/>）。
/// </remarks>
public sealed class LayoutNode
{
    /// <summary>
    /// 稳定的布局标识（GUID 的前 12 位十六进制字符），与终端进程无关；在整个 <see cref="TerminalLayout"/> 内唯一。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>
    /// 窗格绑定的 daemon 会话标识；空窗格为 <see langword="null"/>，分割节点必须为 <see langword="null"/>。
    /// </summary>
    public string? SessionId { get; set; }
    /// <summary>
    /// 分割方向；窗格为 <see langword="null"/>。
    /// </summary>
    public SplitAxis? Axis { get; set; }
    /// <summary>
    /// 分配给第一个子节点的比例（0.1–0.9，默认 0.5），只对分割节点有意义。
    /// </summary>
    /// <remarks>
    /// 计算几何时先从区域长度中扣除 1 个单元格的分隔线，再把剩余长度乘以该比例并向下取整分给第一个子节点（见 <see cref="LayoutGeometry"/>）。
    /// </remarks>
    public double Ratio { get; set; } = 0.5;
    /// <summary>
    /// 第一个子节点：水平分割时在左侧，垂直分割时在上方；窗格为 <see langword="null"/>。
    /// </summary>
    public LayoutNode? First { get; set; }
    /// <summary>
    /// 第二个子节点：水平分割时在右侧，垂直分割时在下方；窗格为 <see langword="null"/>。
    /// </summary>
    public LayoutNode? Second { get; set; }
    /// <summary>
    /// 是否为显示终端的窗格（即 <see cref="Axis"/> 为 <see langword="null"/>）。不参与序列化。
    /// </summary>
    [JsonIgnore] public bool IsPane => Axis is null;
}
