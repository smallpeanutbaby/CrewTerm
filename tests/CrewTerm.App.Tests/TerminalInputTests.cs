using CrewTerm.App.UI;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests;

public sealed class TerminalInputTests
{
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

    [Fact]
    public void NavigationKeysPreserveModifiers()
    {
        Assert.Equal("\u001b[A", TerminalInput.Encode(Key.CursorUp));
        Assert.Equal("\u001b[1;6A", TerminalInput.Encode(Key.CursorUp.WithShift.WithCtrl));
        Assert.Equal("\u001b[3~", TerminalInput.Encode(Key.Delete));
        Assert.Equal("\u001b[Z", TerminalInput.Encode(Key.Tab.WithShift));
        Assert.Equal("\u001bb", TerminalInput.Encode(Key.B.WithAlt));
    }

    [Fact]
    public void PaletteFindsCommandsByChineseOrEnglishWithoutCaseSensitivity()
    {
        var commands = new[] { new PaletteCommand("files", "文件浏览 Files", () => { }), new PaletteCommand("tasks", "运行任务 Tasks", () => { }) };
        Assert.Equal("files", Assert.Single(CommandPalette.Filter(commands, "FILES")).Id);
        Assert.Equal("tasks", Assert.Single(CommandPalette.Filter(commands, "任务")).Id);
        Assert.Empty(CommandPalette.Filter(commands, "not-a-command"));
    }
}
