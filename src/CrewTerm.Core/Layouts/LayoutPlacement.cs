namespace CrewTerm.Core.Layouts;

/// <summary>
/// <see cref="LayoutGeometry.Measure"/> 的一项结果：一个窗格或一条可拖动的分隔线的位置，分隔线还附带其分割区域。
/// </summary>
/// <param name="NodeId">窗格或分割节点的 <see cref="LayoutNode.Id"/>；分隔线使用所属分割节点的标识。</param>
/// <param name="Bounds">窗格或分隔线占据的矩形。</param>
/// <param name="IsDivider">为 <see langword="true"/> 表示分隔线，否则为窗格。</param>
/// <param name="Axis">分隔线所属分割节点的方向；窗格为 <see langword="null"/>。</param>
/// <param name="ParentBounds">
/// 分隔线所属分割节点占据的整个区域，拖动分隔线时用来把鼠标位置换算成 <see cref="LayoutNode.Ratio"/>；窗格为 <see langword="null"/>。
/// </param>
public sealed record LayoutPlacement(string NodeId, LayoutRect Bounds, bool IsDivider,
    SplitAxis? Axis = null, LayoutRect? ParentBounds = null);
