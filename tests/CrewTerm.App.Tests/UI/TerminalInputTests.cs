using CrewTerm.App.UI.Controls;
using CrewTerm.App.UI.Input;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalInput.Encode"/>（把 Terminal.Gui 按键编码为发给子程序的终端字节序列）与 <see cref="CommandPalette.Filter"/> 的单元测试。
/// </summary>
public sealed class TerminalInputTests
{
    /// <summary>
    /// 验证：普通字母（含 Shift 大写）与中文字符原样编码，Ctrl+C 编码为 ETX（<c>\u0003</c>），Enter 编码为回车 <c>\r</c>，Backspace 编码为 DEL（<c>\u007f</c>）。
    /// </summary>
    [Fact]
    public void LettersUnicodeAndControlKeysReachChildUnchanged()
    {
        Assert.Equal("a", TerminalInput.Encode(Key.A));
        Assert.Equal("A", TerminalInput.Encode(Key.A.WithShift));
        Assert.Equal("中", TerminalInput.Encode(new Key('中')));
        Assert.Equal("\u0003", TerminalInput.Encode(Key.C.WithCtrl));
        Assert.Equal("\r", TerminalInput.Encode(Key.Enter));
        Assert.Equal("\u007f", TerminalInput.Encode(Key.Backspace));
    }

    /// <summary>
    /// 验证：方向键编码为 CSI 序列并保留修饰键（Ctrl+Shift+上 为 <c>ESC[1;6A</c>），Delete 为 <c>ESC[3~</c>，Shift+Tab 为 <c>ESC[Z</c>，Alt+B 为 ESC 前缀加 <c>b</c>。
    /// </summary>
    [Fact]
    public void NavigationKeysPreserveModifiers()
    {
        Assert.Equal("\u001b[A", TerminalInput.Encode(Key.CursorUp));
        Assert.Equal("\u001b[1;6A", TerminalInput.Encode(Key.CursorUp.WithShift.WithCtrl));
        Assert.Equal("\u001b[3~", TerminalInput.Encode(Key.Delete));
        Assert.Equal("\u001b[Z", TerminalInput.Encode(Key.Tab.WithShift));
        Assert.Equal("\u001bb", TerminalInput.Encode(Key.B.WithAlt));
    }

    /// <summary>
    /// 验证：命令面板过滤按英文匹配时不区分大小写（<c>FILES</c> 命中 <c>files</c>），也能按中文匹配（<c>任务</c> 命中 <c>tasks</c>），没有匹配时结果为空。
    /// </summary>
    [Fact]
    public void PaletteFindsCommandsByChineseOrEnglishWithoutCaseSensitivity()
    {
        var commands = new[] { new PaletteCommand("files", "文件浏览 Files", () => { }), new PaletteCommand("tasks", "运行任务 Tasks", () => { }) };
        Assert.Equal("files", Assert.Single(CommandPalette.Filter(commands, "FILES")).Id);
        Assert.Equal("tasks", Assert.Single(CommandPalette.Filter(commands, "任务")).Id);
        Assert.Empty(CommandPalette.Filter(commands, "not-a-command"));
    }
}
