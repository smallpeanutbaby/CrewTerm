using CrewTerm.App.UI.Controls;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 覆盖 <see cref="TabStripLayout"/>：标签栏宽度不足时显示的总是包含活动标签的一段连续标签。
/// </summary>
public sealed class TabStripLayoutTests
{
    /// <summary>
    /// 验证：宽度为 [10, 15, 5]、可用 20 列时，从第一个标签开始放，第二个放不下就停止，不会跳过它显示第三个（否则标签栏显示 1、3，序号对不上）。
    /// </summary>
    [Fact]
    public void StopsAtTheFirstTabThatDoesNotFit()
    {
        Assert.Equal((0, 1), TabStripLayout.VisibleRange([10, 15, 5], 20, active: 0));
        Assert.Equal((0, 3), TabStripLayout.VisibleRange([10, 15, 5], 30, active: 0));
    }

    /// <summary>
    /// 验证：活动标签不在从头开始的一段中时，改为以活动标签结尾向前放；起点标签本身比可用宽度还宽时仍显示它一个；没有标签时为 (0, 0)。
    /// </summary>
    [Fact]
    public void KeepsTheActiveTabVisible()
    {
        Assert.Equal((1, 2), TabStripLayout.VisibleRange([10, 15, 5], 20, active: 2));
        Assert.Equal((1, 1), TabStripLayout.VisibleRange([10, 24, 5], 12, active: 1));
        Assert.Equal((0, 0), TabStripLayout.VisibleRange([], 20, active: 0));
    }
}
