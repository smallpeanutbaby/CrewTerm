using CrewTerm.App.UI.Controls;
using CrewTerm.Terminal.Emulation;
using Terminal.Gui.Drawing;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalPane.GetCellAttribute"/>（把终端单元格样式转换为 Terminal.Gui 绘制属性）的单元测试。
/// </summary>
public sealed class TerminalPaneStyleTests
{
    /// <summary>
    /// 验证：带隐藏（concealed）属性的单元格以背景色作为前景色绘制，从而使文字不可见。
    /// </summary>
    [Fact]
    public void ConcealedCellsUseBackgroundColorForTheirForeground()
    {
        var attribute = TerminalPane.GetCellAttribute(new("secret", 0x123456, 0x654321, false, Hidden: true));
        Assert.Equal(attribute.Background, attribute.Foreground);
    }
    /// <summary>
    /// 验证：粗体、淡色、斜体、下划线、闪烁、删除线全部映射为对应的 <see cref="TextStyle"/> 组合，前景色与背景色按 RGB 值原样传递。
    /// </summary>
    [Fact]
    public void RenderingCarriesAllCommonStylesToTerminalGui()
    {
        var cell = new TerminalCell("A", 0x123456, 0x654321, true, Faint: true, Italic: true,
            Underline: true, Blink: true, Strikethrough: true);
        var attribute = TerminalPane.GetCellAttribute(cell);
        Assert.Equal(TextStyle.Bold | TextStyle.Faint | TextStyle.Italic | TextStyle.Underline | TextStyle.Blink | TextStyle.Strikethrough,
            attribute.Style);
        Assert.Equal(new Color(0x123456), attribute.Foreground);
        Assert.Equal(new Color(0x654321), attribute.Background);
    }
}
