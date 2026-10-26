namespace CrewTerm.Core.Layouts;

/// <summary>
/// 以单元格为单位计算分屏布局的几何位置，与 UI 框架无关。
/// </summary>
public static class LayoutGeometry
{
    /// <summary>
    /// 在给定尺寸内为每个窗格和每条分隔线分配矩形。
    /// </summary>
    /// <remarks>
    /// 每个分割节点占用 1 个单元格宽（或高）的分隔线（区域长度为 0 时分隔线也为 0），剩余长度按
    /// <see cref="LayoutNode.Ratio"/> 向下取整分给第一个子节点，余下的给第二个子节点。区域很小时子区域可能为 0。
    /// 结果按中序排列：第一个子树、分隔线、第二个子树。
    /// </remarks>
    /// <param name="root">分屏树的根。</param>
    /// <param name="width">可用宽度（单元格，不小于 0）。</param>
    /// <param name="height">可用高度（单元格，不小于 0）。</param>
    /// <returns>每个窗格与分隔线的位置，坐标相对于区域左上角 (0, 0)。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">宽度或高度为负数（参数名统一报告为 <paramref name="width"/>）。</exception>
    public static IReadOnlyList<LayoutPlacement> Measure(LayoutNode root, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var result = new List<LayoutPlacement>();
        Place(root, new(0, 0, width, height), result);
        return result;
    }

    /// <summary>
    /// 递归地把一个节点放进给定矩形：窗格直接占满矩形；分割节点切出两个子区域与中间的分隔线。
    /// </summary>
    /// <param name="node">要放置的节点；分割节点必须有两个子节点。</param>
    /// <param name="box">节点占据的矩形。</param>
    /// <param name="result">按中序追加放置结果的列表。</param>
    private static void Place(LayoutNode node, LayoutRect box, List<LayoutPlacement> result)
    {
        if (node.IsPane) { result.Add(new(node.Id, box, false)); return; }
        var horizontal = node.Axis == SplitAxis.Horizontal;
        var length = horizontal ? box.Width : box.Height;
        var gap = length > 0 ? 1 : 0;
        var available = length - gap;
        var first = (int)Math.Floor(available * node.Ratio);
        var second = available - first;
        var firstBox = horizontal ? box with { Width = first } : box with { Height = first };
        var divider = horizontal ? new LayoutRect(box.X + first, box.Y, gap, box.Height)
            : new LayoutRect(box.X, box.Y + first, box.Width, gap);
        var secondBox = horizontal ? new LayoutRect(box.X + first + gap, box.Y, second, box.Height)
            : new LayoutRect(box.X, box.Y + first + gap, box.Width, second);
        Place(node.First!, firstBox, result);
        result.Add(new(node.Id, divider, true, node.Axis, box));
        Place(node.Second!, secondBox, result);
    }
}
