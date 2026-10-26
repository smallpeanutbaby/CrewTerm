using CrewTerm.Terminal;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI;

public static class TerminalMouse
{
    public static string? Encode(Mouse mouse, TerminalSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.SgrMouse || snapshot.MouseTracking is not (1000 or 1002 or 1003) ||
            mouse.Position is not { } position || position.X < 0 || position.Y < 0 ||
            position.X >= snapshot.Columns || position.Y >= snapshot.Rows)
            return null;

        var flags = mouse.Flags;
        // Terminal.Gui emits synthetic clicks after the raw press/release pair.
        if (mouse.IsSingleDoubleOrTripleClicked || flags.HasFlag(MouseFlags.PositionReport)) return null;
        var button = (flags & (MouseFlags.LeftButtonPressed | MouseFlags.LeftButtonReleased)) != 0 ? 0 :
            (flags & (MouseFlags.MiddleButtonPressed | MouseFlags.MiddleButtonReleased)) != 0 ? 1 :
            (flags & (MouseFlags.RightButtonPressed | MouseFlags.RightButtonReleased)) != 0 ? 2 :
            flags.HasFlag(MouseFlags.WheeledUp) ? 64 : flags.HasFlag(MouseFlags.WheeledDown) ? 65 : -1;
        if (button < 0) return null;
        if (flags.HasFlag(MouseFlags.Shift)) button += 4;
        if (flags.HasFlag(MouseFlags.Alt)) button += 8;
        if (flags.HasFlag(MouseFlags.Ctrl)) button += 16;
        var terminator = mouse.IsReleased ? 'm' : 'M';
        return FormattableString.Invariant($"\u001b[<{button};{position.X + 1};{position.Y + 1}{terminator}");
    }
}
