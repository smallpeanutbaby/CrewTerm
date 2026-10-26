using CrewTerm.Terminal.Emulation;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Input;

/// <summary>
/// 把 Terminal.Gui 鼠标事件编码为 xterm 鼠标上报序列，转发给开启了鼠标追踪的子程序。
/// </summary>
/// <remarks>
/// 只处理子程序通过 DECSET 1000（按下/释放）、1002（按键拖动）或 1003（任意移动）开启的追踪模式；
/// 坐标格式由 1006（SGR）与 1005（UTF-8）两个扩展决定，状态都来自屏幕快照。
/// </remarks>
public static class TerminalMouse
{
    /// <summary>
    /// 编码一个鼠标事件。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>按键码：左键 0、中键 1、右键 2；滚轮上 64、下 65、左 66、右 67；没有按键的移动为 3。</description></item>
    /// <item><description>移动事件加 32；Shift 加 4，Alt 加 8，Ctrl 加 16（水平滚轮不附加 Ctrl）。</description></item>
    /// <item><description>1000 模式丢弃所有移动事件；1002 模式只上报按住按键时的移动；1003 模式上报全部移动。</description></item>
    /// <item><description>SGR 格式为 <c>CSI &lt; b;x;y M</c>，释放时结尾为 <c>m</c>，坐标从 1 开始的十进制数，不受区域性影响。</description></item>
    /// <item><description>
    /// 传统格式为 <c>CSI M</c> 后跟三个字符（32 + 按键码、33 + 列、33 + 行），释放一律报告为按键码 3；
    /// 坐标上限为 222（启用 UTF-8 扩展时为 2014），超出上限的事件无法编码。
    /// </description></item>
    /// </list>
    /// Terminal.Gui 在原始按下/释放之后额外合成的单击、双击、三击事件会被忽略，避免重复上报。
    /// </remarks>
    /// <param name="mouse">Terminal.Gui 鼠标事件，<see cref="Mouse.Position"/> 为相对终端视口的字符坐标（从 0 开始）。</param>
    /// <param name="snapshot">当前屏幕快照，提供追踪模式、编码扩展与屏幕尺寸；为 <see langword="null"/> 时不编码。</param>
    /// <returns>
    /// 要发送给子程序的序列；没有快照、未开启追踪、坐标在屏幕外或超出编码上限、
    /// 当前模式不上报该事件或事件无法识别时返回 <see langword="null"/>。
    /// </returns>
    public static string? Encode(Mouse mouse, TerminalSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.MouseTracking is not (1000 or 1002 or 1003) ||
            mouse.Position is not { } position || position.X < 0 || position.Y < 0 ||
            position.X >= snapshot.Columns || position.Y >= snapshot.Rows)
            return null;

        var flags = mouse.Flags;
        // Terminal.Gui 会在原始的按下/释放事件之后再合成一次单击（或双击、三击）事件，这里忽略它们以免重复上报。
        if (mouse.IsSingleDoubleOrTripleClicked) return null;
        var motion = flags.HasFlag(MouseFlags.PositionReport);
        if (motion && (snapshot.MouseTracking == 1000 || snapshot.MouseTracking == 1002 && !mouse.IsPressed)) return null;
        var horizontal = flags.HasFlag(MouseFlags.WheeledLeft) || flags.HasFlag(MouseFlags.WheeledRight);
        var button = (flags & (MouseFlags.LeftButtonPressed | MouseFlags.LeftButtonReleased)) != 0 ? 0 :
            (flags & (MouseFlags.MiddleButtonPressed | MouseFlags.MiddleButtonReleased)) != 0 ? 1 :
            (flags & (MouseFlags.RightButtonPressed | MouseFlags.RightButtonReleased)) != 0 ? 2 :
            flags.HasFlag(MouseFlags.WheeledLeft) ? 66 : flags.HasFlag(MouseFlags.WheeledRight) ? 67 :
            flags.HasFlag(MouseFlags.WheeledUp) ? 64 : flags.HasFlag(MouseFlags.WheeledDown) ? 65 : motion ? 3 : -1;
        if (button < 0) return null;
        if (!snapshot.SgrMouse && mouse.IsReleased) button = 3;
        if (motion) button += 32;
        if (flags.HasFlag(MouseFlags.Shift)) button += 4;
        if (flags.HasFlag(MouseFlags.Alt)) button += 8;
        if (!horizontal && flags.HasFlag(MouseFlags.Ctrl)) button += 16;
        if (!snapshot.SgrMouse)
        {
            var maximum = snapshot.Utf8Mouse ? 2014 : 222;
            if (position.X > maximum || position.Y > maximum) return null;
            return $"\u001b[M{(char)(32 + button)}{(char)(33 + position.X)}{(char)(33 + position.Y)}";
        }
        var terminator = mouse.IsReleased ? 'm' : 'M';
        return FormattableString.Invariant($"\u001b[<{button};{position.X + 1};{position.Y + 1}{terminator}");
    }
}
