using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Input;

/// <summary>
/// 把 Terminal.Gui 按键转换为发送给子程序的 VT/xterm 输入序列。
/// </summary>
public static class TerminalInput
{
    /// <summary>
    /// 编码一个按键。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>方向键 / Home / End：无修饰时为 <c>CSI x</c>（应用光标模式下为 <c>SS3 x</c>），有修饰时为 <c>CSI 1;m x</c>。</description></item>
    /// <item><description>Insert、Delete、PageUp/Down、F5–F12：<c>CSI n~</c> 或 <c>CSI n;m~</c>。</description></item>
    /// <item><description>F1–F4：<c>SS3 P..S</c>；Backspace 为 DEL（0x7F）；Shift+Tab 为 <c>CSI Z</c>。</description></item>
    /// <item><description>Enter 为 CR（0x0D），Esc 为 ESC（0x1B），Tab 为 HT（0x09）；这三个键与 F1–F4、Backspace 不编码 Shift/Ctrl 修饰（Shift+Tab 除外），Alt 仍会前缀 ESC。</description></item>
    /// <item><description>Ctrl+字母为对应控制字符，Ctrl+Space 为 NUL；Alt 组合在前面加 ESC。</description></item>
    /// </list>
    /// 修饰值 m = 1 + Shift(1) + Alt(2) + Ctrl(4)，与 xterm 一致。
    /// </remarks>
    /// <param name="key">按键。</param>
    /// <param name="applicationCursor">子程序是否启用了应用光标键模式（DECCKM）。</param>
    /// <returns>要发送的文本；无法编码的按键返回 <see langword="null"/>，交给工作台处理。</returns>
    public static string? Encode(Key key, bool applicationCursor = false)
    {
        var code = key.NoAlt.NoCtrl.NoShift.KeyCode;
        var modifier = 1 + (key.IsShift ? 1 : 0) + (key.IsAlt ? 2 : 0) + (key.IsCtrl ? 4 : 0);
        var direction = code switch
        {
            KeyCode.CursorUp => "A", KeyCode.CursorDown => "B", KeyCode.CursorRight => "C",
            KeyCode.CursorLeft => "D", KeyCode.Home => "H", KeyCode.End => "F", _ => null
        };
        if (direction is not null)
            return modifier != 1 ? $"\u001b[1;{modifier}{direction}" : $"\u001b{(applicationCursor ? "O" : "[")}{direction}";
        var number = code switch
        {
            KeyCode.Insert => 2, KeyCode.Delete => 3, KeyCode.PageUp => 5, KeyCode.PageDown => 6,
            KeyCode.F5 => 15, KeyCode.F6 => 17, KeyCode.F7 => 18, KeyCode.F8 => 19,
            KeyCode.F9 => 20, KeyCode.F10 => 21, KeyCode.F11 => 23, KeyCode.F12 => 24, _ => 0
        };
        if (number > 0) return modifier == 1 ? $"\u001b[{number}~" : $"\u001b[{number};{modifier}~";
        var text = code switch
        {
            KeyCode.Enter => "\r", KeyCode.Esc => "\u001b", KeyCode.Tab => key.IsShift ? "\u001b[Z" : "\t",
            KeyCode.Backspace => "\u007f", KeyCode.F1 => "\u001bOP", KeyCode.F2 => "\u001bOQ",
            KeyCode.F3 => "\u001bOR", KeyCode.F4 => "\u001bOS", _ => null
        };
        if (text is null)
        {
            if (key.IsCtrl && code >= KeyCode.A && code <= KeyCode.Z) text = ((char)((uint)code - (uint)KeyCode.A + 1)).ToString();
            else if (key.IsCtrl && code == KeyCode.Space) text = "\0";
            // 去掉 Alt 后再取可打印字符，使 Alt+B 编码为 ESC b 而不是 null。
            else if (key.NoAlt.TryGetPrintableRune(out var rune)) text = rune.ToString();
        }
        return text is not null && key.IsAlt ? "\u001b" + text : text;
    }
}
