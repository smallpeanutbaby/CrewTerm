using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI;

public static class TerminalInput
{
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
            else if (key.NoAlt.TryGetPrintableRune(out var rune)) text = rune.ToString();
        }
        return text is not null && key.IsAlt ? "\u001b" + text : text;
    }
}
