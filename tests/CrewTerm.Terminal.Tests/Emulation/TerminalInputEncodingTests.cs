using System.Text;
using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Tests.Emulation;

/// <summary>
/// 覆盖 <see cref="TerminalScreen.EncodeInput"/> 把待发送输入编码为字节的规则：只有在已协商传统鼠标模式时，
/// 格式正确的 6 字符鼠标报告才按原始 8 位字节发送，其余情况一律按 UTF-8 编码。均为纯内存单元测试。
/// </summary>
public sealed class TerminalInputEncodingTests
{
    /// <summary>
    /// 验证：开启鼠标跟踪 1000 且未启用 SGR/UTF-8 扩展时，坐标字节超过 127（U+00B7，即 183）的传统鼠标报告
    /// 被编码为恰好 6 个原始字节，而不是把高位字符展开成两个 UTF-8 字节。
    /// </summary>
    [Fact]
    public void LegacyMousePacketUsesExactlySixRawBytesAtExtendedCoordinates()
    {
        var screen = new TerminalScreen(200, 30);
        screen.Feed("\u001b[?1000h");
        Assert.Equal(new byte[] { 27, 91, 77, 32, 183, 39 }, screen.EncodeInput("\u001b[M \u00b7'"));
    }

    /// <summary>
    /// 验证：即使已开启鼠标跟踪 1000，普通文本（中文与 Emoji）、报告后附带多余字符、按键字节为 NUL、
    /// 坐标字符超出单字节范围（U+0100）这些不是合法 6 字符鼠标报告的输入，仍全部按 UTF-8 编码。
    /// </summary>
    /// <param name="input">待编码的输入文本。</param>
    [Theory]
    [InlineData("中文👩‍💻")]
    [InlineData("\u001b[M \u00b7'extra")]
    [InlineData("\u001b[M\0ab")]
    [InlineData("\u001b[M \u0100a")]
    public void OrdinaryAndInvalidPacketTextAlwaysUsesUtf8(string input)
    {
        var screen = new TerminalScreen(200, 30);
        screen.Feed("\u001b[?1000h");
        Assert.Equal(Encoding.UTF8.GetBytes(input), screen.EncodeInput(input));
    }

    /// <summary>
    /// 验证：未开启鼠标跟踪，或在鼠标跟踪 1000 之外还启用了 SGR（1006）或 UTF-8（1005）坐标编码时，
    /// 同样的鼠标报告文本按 UTF-8 编码，不会被当作传统 8 位报告发送。
    /// </summary>
    /// <param name="modes">预先输入屏幕的模式设置序列；空字符串表示不开启任何鼠标模式。</param>
    [Theory]
    [InlineData("")]
    [InlineData("\u001b[?1000;1006h")]
    [InlineData("\u001b[?1000;1005h")]
    public void OnlyNegotiatedLegacyModeSelectsEightBitEncoding(string modes)
    {
        var screen = new TerminalScreen(200, 30);
        screen.Feed(modes);
        Assert.Equal(Encoding.UTF8.GetBytes("\u001b[M \u00b7'"), screen.EncodeInput("\u001b[M \u00b7'"));
    }
}
