using CrewTerm.Terminal;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace CrewTerm.App.UI;

public sealed class TerminalPane : View
{
    public string? SessionId { get; set; }
    public TerminalSnapshot? Snapshot { get; private set; }
    public Action<string>? SendInput { get; set; }
    public TerminalPane()
    {
        CanFocus = true;
        Width = Dim.Fill(); Height = Dim.Fill();
        SetScheme(new Scheme { Normal = new(new Color(0xd8dee9), new Color(0x10151d)) });
        KeyDown += (_, key) =>
        {
            var text = TerminalInput.Encode(key, Snapshot?.ApplicationCursorKeys ?? false);
            if (text is null) return;
            key.Handled = true;
            SendInput?.Invoke(text);
        };
        MouseEvent += (_, mouse) =>
        {
            if (mouse.Position is not { } position || position.X < 0 || position.Y < 0 ||
                position.X >= Viewport.Width || position.Y >= Viewport.Height) return;
            if (mouse.IsSingleDoubleOrTripleClicked) { mouse.Handled = true; return; }
            if (mouse.IsPressed && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
            { SetFocus(); mouse.Handled = true; }
            if (SessionId is null || TerminalMouse.Encode(mouse, Snapshot) is not string text) return;
            mouse.Handled = true;
            SendInput?.Invoke(text);
        };
    }

    public void Update(TerminalSnapshot? snapshot)
    {
        Snapshot = snapshot;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            Move(1, 1); AddStr("F3  Start an agent or shell");
            Move(1, 3); AddStr("Each terminal has its own process and working directory.");
            return true;
        }
        for (var y = 0; y < Math.Min(Viewport.Height, snapshot.Rows); y++)
        for (var x = 0; x < Math.Min(Viewport.Width, snapshot.Columns); x++)
        {
            var cell = snapshot.Cells[y][x];
            if (cell.Width == 0) continue;
            var attribute = new Attribute(new Color(cell.Foreground), new Color(cell.Background), cell.Bold ? TextStyle.Bold : TextStyle.None);
            SetAttribute(attribute); Move(x, y); AddStr(cell.Text);
        }
        if (HasFocus && snapshot.CursorVisible)
        {
            var position = ViewportToScreen().Location;
            Cursor = new() { Position = new(position.X + snapshot.CursorColumn, position.Y + snapshot.CursorRow), Style = CursorStyle.BlinkingBar };
        }
        else Cursor = new() { Position = null };
        return true;
    }
}
