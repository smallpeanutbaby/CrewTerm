namespace CrewTerm.Core.Layouts;

/// <summary>
/// 分割节点中两个子区域的排列方向。
/// </summary>
public enum SplitAxis
{
    /// <summary>
    /// 子区域从左到右排列，中间是一条竖直的分隔线；比例作用于宽度。
    /// </summary>
    Horizontal,
    /// <summary>
    /// 子区域从上到下排列，中间是一条水平的分隔线；比例作用于高度。
    /// </summary>
    Vertical
}
