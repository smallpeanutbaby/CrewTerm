namespace CrewTerm.Terminal;

public sealed partial class TerminalScreen
{
    private static readonly int[] Palette =
    [
        0x0C0C0C, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF
    ];

    private void ApplySgr(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            switch (value)
            {
                case 0: _foreground = DefaultForeground; _background = DefaultBackground; _bold = _reverse = false; break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case 7: _reverse = true; break;
                case 27: _reverse = false; break;
                case 39: _foreground = DefaultForeground; break;
                case 49: _background = DefaultBackground; break;
                case >= 30 and <= 37: _foreground = Palette[value - 30]; break;
                case >= 40 and <= 47: _background = Palette[value - 40]; break;
                case >= 90 and <= 97: _foreground = Palette[value - 90 + 8]; break;
                case >= 100 and <= 107: _background = Palette[value - 100 + 8]; break;
                case 38: case 48:
                    int? color = null;
                    if (i + 2 < values.Length && values[i + 1] == 5)
                    {
                        color = IndexedColor(Math.Clamp(values[i + 2], 0, 255));
                        i += 2;
                    }
                    else if (i + 4 < values.Length && values[i + 1] == 2)
                    {
                        color = Math.Clamp(values[i + 2], 0, 255) << 16
                            | Math.Clamp(values[i + 3], 0, 255) << 8 | Math.Clamp(values[i + 4], 0, 255);
                        i += 4;
                    }
                    if (color is { } rgb) { if (value == 38) _foreground = rgb; else _background = rgb; }
                    break;
            }
        }
    }

    private static int IndexedColor(int index)
    {
        if (index < 16) return Palette[index];
        if (index >= 232) { var gray = 8 + (index - 232) * 10; return gray << 16 | gray << 8 | gray; }
        var value = index - 16;
        static int Component(int n) => n == 0 ? 0 : 55 + n * 40;
        return Component(value / 36) << 16 | Component(value / 6 % 6) << 8 | Component(value % 6);
    }
}
