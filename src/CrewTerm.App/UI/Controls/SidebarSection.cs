using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// herdr 风格的侧栏分区：第一行是小写标题与可点击的 <c>+ new</c>，其下每行为“状态符号  名称  暗色说明”。
/// </summary>
/// <remarks>
/// <para>用于左侧的 <c>spaces</c>（项目）与 <c>agents</c>（会话）两个分区。没有任何行时显示一行可点击的占位提示。</para>
/// <para>交互：方向键、Home/End、PageUp/PageDown 移动选择，Enter 激活，<c>+</c> 新建，Shift+F10 打开选中行的菜单，Esc 把焦点还给终端；
/// 鼠标左键单击一行即选中并激活，右键打开该行的菜单，单击 <c>+ new</c> 或占位提示即新建，滚轮滚动列表。</para>
/// <para>鼠标按最近一次<b>绘制出来</b>的行判断点中了哪一行：行内容在两次绘制之间可能被刷新（例如 Agent 状态变化导致重新排序），
/// 用户点击的应当是他看到的那一行。回调收到的是行本身，调用方按 <see cref="SidebarRow.Key"/> 找回对象。</para>
/// <para>所有回调都在 UI 线程触发；本视图不保存任何业务状态，由工作台通过 <see cref="SetRows"/> 刷新内容。</para>
/// </remarks>
public sealed class SidebarSection : View
{
    /// <summary>
    /// 背景色，与工作台窗口一致。
    /// </summary>
    private const int Background = 0x10151d;

    /// <summary>
    /// 获得焦点时选中行的背景色。
    /// </summary>
    private const int SelectedBackground = 0x25425c;

    /// <summary>
    /// 普通名称的颜色。
    /// </summary>
    private const int TitleColor = 0xc6d1df;

    /// <summary>
    /// 当前行名称的颜色（粗体）。
    /// </summary>
    private const int CurrentColor = 0xffffff;

    /// <summary>
    /// 右侧说明与占位提示的暗色。
    /// </summary>
    private const int DetailColor = 0x7f849c;

    /// <summary>
    /// 分区标题的颜色。
    /// </summary>
    private const int HeadingColor = 0x89b4fa;

    /// <summary>
    /// 标题行右侧的新建按钮文本。
    /// </summary>
    private const string NewLabel = "+ new";

    /// <summary>
    /// 名称至少保留的显示宽度；放不下时缩短右侧说明。
    /// </summary>
    private const int MinimumTitleColumns = 6;

    /// <summary>
    /// 当前的行（最近一次 <see cref="SetRows"/> 的内容）。
    /// </summary>
    private IReadOnlyList<SidebarRow> rows = [];

    /// <summary>
    /// 最近一次绘制出来的行，鼠标按它判断点中了哪一行。
    /// </summary>
    private IReadOnlyList<SidebarRow> drawnRows = [];

    /// <summary>
    /// 最近一次绘制时第一个可见行的索引。
    /// </summary>
    private int drawnTop;

    /// <summary>
    /// 选中行的索引；没有行时为 0。
    /// </summary>
    private int selected;

    /// <summary>
    /// 列表滚动位置：第一个可见行的索引。
    /// </summary>
    private int top;

    /// <summary>
    /// 小写的分区标题，例如 <c>spaces</c>。
    /// </summary>
    public string Heading { get; }

    /// <summary>
    /// 没有任何行时显示的占位提示（前面自动加 <c>+ </c>），例如 <c>add a project  F2</c>。
    /// </summary>
    public string EmptyText { get; }

    /// <summary>
    /// 一行被激活（Enter 或左键单击）时调用，参数为该行。
    /// </summary>
    public Action<SidebarRow>? RowActivated { get; set; }

    /// <summary>
    /// 请求打开某行的菜单（右键或 Shift+F10）时调用，参数为该行。
    /// </summary>
    public Action<SidebarRow>? ContextRequested { get; set; }

    /// <summary>
    /// 点击 <c>+ new</c>、点击占位提示、在空列表中按 Enter 或按 <c>+</c> 时调用。
    /// </summary>
    public Action? CreateRequested { get; set; }

    /// <summary>
    /// 在分区中按 Esc 时调用，由工作台把焦点还给终端（Esc 不再落到 Terminal.Gui 默认的“退出”命令上）。
    /// </summary>
    public Action? FocusReturnRequested { get; set; }

    /// <summary>
    /// 当前显示的行。
    /// </summary>
    public IReadOnlyList<SidebarRow> Rows => rows;

    /// <summary>
    /// 选中行的索引；没有行时为 -1。
    /// </summary>
    public int SelectedIndex => rows.Count == 0 ? -1 : selected;

    /// <summary>
    /// 第一个可见行的索引（滚动位置）。
    /// </summary>
    public int FirstVisibleIndex => top;

    /// <summary>
    /// 创建一个可获得焦点的侧栏分区。
    /// </summary>
    /// <param name="heading">小写的分区标题。</param>
    /// <param name="emptyText">没有任何行时的占位提示。</param>
    public SidebarSection(string heading, string emptyText)
    {
        Heading = heading;
        EmptyText = emptyText;
        CanFocus = true;
        HasFocusChanged += (_, _) => SetNeedsDraw();
        KeyDown += (_, key) => key.Handled = HandleKey(key);
        MouseEvent += (_, mouse) => HandleMouse(mouse);
    }

    /// <summary>
    /// 替换显示的行。
    /// </summary>
    /// <remarks>
    /// 只有选择确实改变时才滚动到选中行；否则保持用户用滚轮滚到的位置，只在行数变少时收回越界的滚动。
    /// </remarks>
    /// <param name="value">新的行。</param>
    /// <param name="select">要选中的行索引；为 <see langword="null"/> 时保持原索引。超出范围时取最近的有效行。</param>
    public void SetRows(IReadOnlyList<SidebarRow> value, int? select = null)
    {
        var previous = selected;
        rows = value;
        selected = Math.Clamp(select ?? selected, 0, Math.Max(0, rows.Count - 1));
        if (selected != previous) EnsureVisible();
        else ClampTop();
        SetNeedsDraw();
    }

    /// <summary>
    /// 把文本截断到指定显示宽度（按终端列计算，宽字符占两列），超出时以 <c>…</c> 结尾。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="columns">最大显示宽度。</param>
    /// <returns>显示宽度不超过 <paramref name="columns"/> 的文本；宽度不大于 0 时为空字符串。</returns>
    public static string Fit(string text, int columns)
    {
        if (columns <= 0) return "";
        if (text.GetColumns() <= columns) return text;
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var width = Math.Max(0, rune.GetColumns());
            if (used + width > columns - 1) break;
            result.Append(rune.ToString());
            used += width;
        }
        return result.Append('…').ToString();
    }

    /// <summary>
    /// 可见的行数（视图高度减去标题行）。
    /// </summary>
    private int VisibleRows => Math.Max(0, Viewport.Height - 1);

    /// <summary>
    /// 调整滚动位置，使选中行可见。
    /// </summary>
    private void EnsureVisible()
    {
        var visible = Math.Max(1, VisibleRows);
        if (selected < top) top = selected;
        if (selected >= top + visible) top = selected - visible + 1;
        ClampTop();
    }

    /// <summary>
    /// 把滚动位置限制在有效范围内（最后一页填满可见区域）。
    /// </summary>
    private void ClampTop() => top = Math.Clamp(top, 0, Math.Max(0, rows.Count - Math.Max(1, VisibleRows)));

    /// <summary>
    /// 选中并激活一行。
    /// </summary>
    /// <param name="index">在 <see cref="rows"/> 中的行索引。</param>
    private void Activate(int index)
    {
        selected = index;
        EnsureVisible();
        SetNeedsDraw();
        RowActivated?.Invoke(rows[index]);
    }

    /// <summary>
    /// 处理键盘：移动选择、激活、新建、打开菜单与把焦点还给终端。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <returns>按键已被处理时为 <see langword="true"/>。</returns>
    private bool HandleKey(Key key)
    {
        if (key == Key.CursorUp || key == Key.CursorDown || key == Key.Home || key == Key.End || key == Key.PageUp || key == Key.PageDown)
        {
            if (rows.Count == 0) return true;
            var page = Math.Max(1, VisibleRows);
            selected = key == Key.CursorUp ? selected - 1 : key == Key.CursorDown ? selected + 1 : key == Key.Home ? 0
                : key == Key.End ? rows.Count - 1 : key == Key.PageUp ? selected - page : selected + page;
            selected = Math.Clamp(selected, 0, rows.Count - 1);
            EnsureVisible();
            SetNeedsDraw();
            return true;
        }
        if (key == Key.Enter)
        {
            if (rows.Count == 0) CreateRequested?.Invoke();
            else Activate(selected);
            return true;
        }
        if (key == Key.Esc) { FocusReturnRequested?.Invoke(); return true; }
        if (key.AsRune.Value == '+') { CreateRequested?.Invoke(); return true; }
        if (key == Key.F10.WithShift && rows.Count > 0) { ContextRequested?.Invoke(rows[selected]); return true; }
        return false;
    }

    /// <summary>
    /// 处理鼠标：单击新建按钮、占位提示或行，右键打开菜单，滚轮滚动。
    /// </summary>
    /// <remarks>
    /// 只响应按下事件；释放以及 Terminal.Gui 合成的单击、双击、三击事件被吞掉，避免同一次点击触发两次操作，也避免默认的激活命令把焦点抢回侧栏。
    /// 点中的行按最近一次绘制的内容判断，再在当前行中按 <see cref="SidebarRow.Key"/> 找回对应的行。
    /// </remarks>
    /// <param name="mouse">鼠标事件，位置为视口坐标。</param>
    private void HandleMouse(Mouse mouse)
    {
        if (mouse.Position is not { } position) return;
        // 释放与合成的单击事件也要标记为已处理：否则 Terminal.Gui 会执行默认的激活命令并把焦点抢回本分区，
        // 而按下时的操作（例如单击空间）刚把焦点交给了终端。
        if (mouse.IsSingleDoubleOrTripleClicked || mouse.IsReleased) { mouse.Handled = true; return; }
        var flags = mouse.Flags;
        if (flags.HasFlag(MouseFlags.WheeledUp) || flags.HasFlag(MouseFlags.WheeledDown))
        {
            top += flags.HasFlag(MouseFlags.WheeledUp) ? -3 : 3;
            ClampTop();
            SetNeedsDraw();
            mouse.Handled = true;
            return;
        }
        var left = flags.HasFlag(MouseFlags.LeftButtonPressed) && !flags.HasFlag(MouseFlags.PositionReport);
        var right = flags.HasFlag(MouseFlags.RightButtonPressed) && !flags.HasFlag(MouseFlags.PositionReport);
        if (!left && !right) return;
        mouse.Handled = true;
        if (!HasFocus) SetFocus();
        if (position.Y == 0)
        {
            if (left && position.X >= Viewport.Width - NewLabel.Length - 1) CreateRequested?.Invoke();
            return;
        }
        var shown = drawnRows.Count > 0 || rows.Count == 0 ? drawnRows : rows;
        if (shown.Count == 0)
        {
            if (left && position.Y == 1) CreateRequested?.Invoke();
            return;
        }
        var drawnIndex = (ReferenceEquals(shown, drawnRows) ? drawnTop : top) + position.Y - 1;
        if (drawnIndex < 0 || drawnIndex >= shown.Count) return;
        var index = rows.ToList().FindIndex(row => row.Key == shown[drawnIndex].Key);
        if (index < 0) return;
        if (left) Activate(index);
        else
        {
            selected = index;
            SetNeedsDraw();
            ContextRequested?.Invoke(rows[index]);
        }
    }

    /// <summary>
    /// 绘制标题行与可见的行，并记下绘制出来的行供鼠标判断。
    /// </summary>
    /// <param name="context">绘制上下文（未使用）。</param>
    /// <returns>始终为 <see langword="true"/>，表示内容已由本视图绘制完毕。</returns>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width < 1 || Viewport.Height < 1) return true;
        ClampTop();
        drawnRows = rows;
        drawnTop = top;
        for (var y = 0; y < Viewport.Height; y++) Paint(0, y, new string(' ', width), TitleColor, Background);
        Paint(1, 0, Fit(Heading, width - NewLabel.Length - 3), HeadingColor, Background, TextStyle.Bold);
        if (width > NewLabel.Length + 2) Paint(width - NewLabel.Length - 1, 0, NewLabel, DetailColor, Background);
        if (rows.Count == 0)
        {
            if (Viewport.Height > 1) Paint(1, 1, Fit("+ " + EmptyText, width - 2), DetailColor, Background);
            return true;
        }
        for (var line = 0; line < VisibleRows && top + line < rows.Count; line++)
        {
            var index = top + line;
            var row = rows[index];
            var y = line + 1;
            var background = HasFocus && index == selected ? SelectedBackground : Background;
            Paint(0, y, new string(' ', width), TitleColor, background);
            Paint(1, y, row.Glyph, row.GlyphColor, background);
            var room = width - 4;
            var titleWidth = Math.Min(row.Title.GetColumns(), Math.Max(MinimumTitleColumns, room / 2));
            // 说明放不下时缩短说明（以 … 结尾），名称至少保留 MinimumTitleColumns 列。
            var detail = row.Detail.Length == 0 ? "" : Fit(row.Detail, room - titleWidth - 1);
            var detailWidth = detail.GetColumns();
            var title = Fit(row.Title, detailWidth == 0 ? room : room - detailWidth - 1);
            Paint(3, y, title, row.Current ? CurrentColor : TitleColor, background, row.Current ? TextStyle.Bold : TextStyle.None);
            if (detailWidth > 0) Paint(width - 1 - detailWidth, y, detail, DetailColor, background);
        }
        return true;
    }

    /// <summary>
    /// 以指定颜色在视口坐标处输出一段文本。
    /// </summary>
    /// <param name="x">列。</param>
    /// <param name="y">行。</param>
    /// <param name="text">文本。</param>
    /// <param name="foreground">前景色（24 位 RGB）。</param>
    /// <param name="background">背景色（24 位 RGB）。</param>
    /// <param name="style">文字样式。</param>
    private void Paint(int x, int y, string text, int foreground, int background, TextStyle style = TextStyle.None)
    {
        if (text.Length == 0 || x >= Viewport.Width) return;
        SetAttribute(new Attribute(new Color(foreground), new Color(background), style));
        Move(x, y);
        AddStr(text);
    }
}
