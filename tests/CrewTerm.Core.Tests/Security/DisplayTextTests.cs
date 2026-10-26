using CrewTerm.Core.Security;

namespace CrewTerm.Core.Tests.Security;

/// <summary>
/// 覆盖 <see cref="DisplayText"/>：外部文本显示到工作台前的控制字符检测与清理（纯单元测试）。
/// </summary>
public sealed class DisplayTextTests
{
    /// <summary>
    /// 验证：<c>ESC</c>、BEL、DEL 与 C1 控制字符都被识别；换行、回车与制表符只在允许换行时放行；普通中英文文本不被误报。
    /// </summary>
    [Fact]
    public void ControlCharactersAreDetectedAndLineBreaksAreOptional()
    {
        Assert.True(DisplayText.ContainsControl("name\u001b]0;title\u0007", allowLineBreaks: true));
        Assert.True(DisplayText.ContainsControl("bell\u0007", allowLineBreaks: true));
        Assert.True(DisplayText.ContainsControl("del\u007f", allowLineBreaks: true));
        Assert.True(DisplayText.ContainsControl("c1\u009b31m", allowLineBreaks: true));
        Assert.True(DisplayText.ContainsControl("a\nb", allowLineBreaks: false));
        Assert.False(DisplayText.ContainsControl("a\r\n\tb", allowLineBreaks: true));
        Assert.False(DisplayText.ContainsControl("Agent A · 中文 ✓", allowLineBreaks: false));
    }

    /// <summary>
    /// 验证：清理去掉颜色等 CSI 序列、以 BEL 或 ST 结束的 OSC 序列和被截断的序列，单独的回车被删除，
    /// 其余控制字符替换为 <c>U+FFFD</c>，换行与制表符保留。
    /// </summary>
    [Fact]
    public void SanitizeRemovesEscapeSequencesAndReplacesOtherControls()
    {
        Assert.Equal("red plain", DisplayText.Sanitize("\u001b[31mred\u001b[0m plain"));
        Assert.Equal("ab", DisplayText.Sanitize("a\u001b]52;c;ZXZpbA==\u0007b"));
        Assert.Equal("ab", DisplayText.Sanitize("a\u001b]0;title\u001b\\b"));
        Assert.Equal("progress done\nnext\tcol", DisplayText.Sanitize("progress\r done\r\nnext\tcol"));
        Assert.Equal("x�y", DisplayText.Sanitize("x\u0007y"));
        Assert.Equal("tail", DisplayText.Sanitize("tail\u001b["));
    }
}
