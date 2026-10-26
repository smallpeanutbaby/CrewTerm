using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Tests.Emulation;

/// <summary>
/// 覆盖 <see cref="TerminalScreen"/> 的基础行为：光标定位、跨多次输入拆分的转义序列、行内擦除与快照隔离。
/// 均为纯内存单元测试；同时提供供其他终端测试类复用的 <see cref="Line"/> 辅助方法。
/// </summary>
public sealed class TerminalScreenTests
{
    /// <summary>
    /// 验证：写两行文本后用 <c>CSI 1;1H</c> 回到左上角写入 <c>X</c>，只覆盖第一行首字符（<c>Xello</c>），
    /// 第二行 <c>world</c> 保持不变，光标停在第 2 列（列索引 1）。
    /// </summary>
    [Fact]
    public void CursorAddressingOverwritesExistingText()
    {
        var screen = new TerminalScreen(10, 3);
        screen.Feed("hello\r\nworld\u001b[1;1HX");
        Assert.Equal("Xello", Line(screen, 0));
        Assert.Equal("world", Line(screen, 1));
        Assert.Equal(1, screen.Snapshot().CursorColumn);
    }

    /// <summary>
    /// 验证：被拆成 <c>ESC [</c>、<c>2D</c> 两次输入的光标左移序列仍能正确执行，随后写入的 <c>X</c> 覆盖第 2 个字符得到 <c>aXc</c>。
    /// </summary>
    [Fact]
    public void EscapeSequencesCanBeSplitAcrossReads()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abc\u001b[");
        screen.Feed("2D");
        screen.Feed("X");
        Assert.Equal("aXc", Line(screen, 0));
    }

    /// <summary>
    /// 验证：光标移到第 4 列后执行 <c>CSI K</c>（擦除到行尾），只清除光标及其右侧的字符，行内容变为 <c>abc</c>。
    /// </summary>
    [Fact]
    public void EraseLineRemovesOnlyRequestedSection()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("abcdef\u001b[4G\u001b[K");
        Assert.Equal("abc", Line(screen, 0));
    }

    /// <summary>
    /// 验证：快照与屏幕状态相互独立——之后写入屏幕的内容不会改变已取得的快照，修改快照中的单元格也不会影响屏幕。
    /// </summary>
    [Fact]
    public void SnapshotsAreDetachedFromFutureWrites()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("before");
        var before = screen.Snapshot();
        screen.Feed("\rAFTER");
        Assert.Equal("b", before.Cells[0][0].Text);
        before.Cells[0][0] = before.Cells[0][0] with { Text = "changed" };
        Assert.Equal("A", screen.Snapshot().Cells[0][0].Text);
    }

    /// <summary>
    /// 取屏幕当前快照中指定行所有单元格的文本，拼接后去掉行尾空白，便于按字符串断言整行内容。
    /// </summary>
    /// <param name="screen">要读取的屏幕。</param>
    /// <param name="index">行索引（从 0 开始，只含可见区域）。</param>
    /// <returns>该行去除行尾空白后的文本。</returns>
    internal static string Line(TerminalScreen screen, int index) =>
        string.Concat(screen.Snapshot().Cells[index].Select(c => c.Text)).TrimEnd();
}
