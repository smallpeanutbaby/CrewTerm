namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 计算标签栏放得下哪些标签：结果总是一段连续的标签，并且包含活动标签。
/// </summary>
/// <remarks>
/// 不依赖界面，便于测试。先从第一个标签开始放；放不下活动标签时，改为以活动标签结尾向前放。
/// 从起点开始依次放置，遇到第一个放不下的标签就停止，不会跳过它去放后面更窄的标签——否则标签栏会漏掉中间的标签，
/// 而 <c>Ctrl+b</c> 加数字仍按真实序号跳转，显示的顺序与序号对不上。
/// </remarks>
public static class TabStripLayout
{
    /// <summary>
    /// 计算可见的一段标签。
    /// </summary>
    /// <param name="widths">各标签的显示宽度（列）。</param>
    /// <param name="room">标签可用的总宽度（列）。</param>
    /// <param name="active">活动标签的索引；超出范围时按 0 处理。</param>
    /// <returns>
    /// 第一个可见标签的索引与可见标签数。宽度不足以放下起点标签时仍显示这一个标签（由视图截断）；没有标签时为 (0, 0)。
    /// </returns>
    public static (int Start, int Count) VisibleRange(IReadOnlyList<int> widths, int room, int active)
    {
        if (widths.Count == 0) return (0, 0);
        active = active < 0 || active >= widths.Count ? 0 : active;
        var start = 0;
        while (start < active && widths.Skip(start).Take(active - start + 1).Sum() > room) start++;
        var limit = Math.Max(room, widths[start]);
        var used = 0;
        var count = 0;
        for (var i = start; i < widths.Count && used + widths[i] <= limit; i++)
        {
            used += widths[i];
            count++;
        }
        return (start, count);
    }
}
