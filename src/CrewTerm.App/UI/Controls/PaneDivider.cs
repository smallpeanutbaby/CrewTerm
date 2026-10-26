using CrewTerm.Core.Layouts;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 两个终端面板之间可用鼠标拖动的一格分隔条。
/// </summary>
/// <remarks>
/// 按下左键时捕获鼠标，拖动过程中按鼠标在父区域中的位置换算比例（限制在 0.1–0.9），
/// 释放左键时结束拖动。比例的保存与重新布局由创建者通过回调完成。
/// </remarks>
internal sealed class PaneDivider : Label
{
    /// <summary>
    /// 是否正在拖动。
    /// </summary>
    private bool dragging;

    /// <summary>
    /// 分隔条当前的位置、方向和所属分割区域；每次重新布局时更新。
    /// </summary>
    public LayoutPlacement Placement { get; set; }

    /// <summary>
    /// 创建分隔条。
    /// </summary>
    /// <param name="placement">初始布局位置，必须是带有父区域（<see cref="LayoutPlacement.ParentBounds"/>）的分隔条位置。</param>
    /// <param name="resized">
    /// 拖动期间每个鼠标事件都在 UI 线程以新的第一子区域比例调用；比例已限制在 0.1–0.9，
    /// 父区域沿分割方向的长度不足 2 格时不调用。
    /// </param>
    public PaneDivider(LayoutPlacement placement, Action<double> resized)
    {
        Placement = placement; CanFocus = false; MousePositionTracking = true;
        SetScheme(new Scheme { Normal = new(new Color(0x5b7595), new Color(0x10151d)) });
        MouseEvent += (_, mouse) =>
        {
            if (mouse.Position is not { } point) return;
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
            { dragging = true; App?.Mouse.GrabMouse(this); mouse.Handled = true; return; }
            if (!dragging) return;
            mouse.Handled = true;
            var parent = Placement.ParentBounds!;
            var horizontal = Placement.Axis == SplitAxis.Horizontal;
            var length = (horizontal ? parent.Width : parent.Height) - 1;
            var offset = horizontal ? Frame.X + point.X - parent.X : Frame.Y + point.Y - parent.Y;
            if (length > 0) resized(Math.Clamp((double)offset / length, 0.1, 0.9));
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) || mouse.IsSingleDoubleOrTripleClicked)
            { dragging = false; App?.Mouse.UngrabMouse(); }
        };
    }
}
