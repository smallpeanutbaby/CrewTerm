using System.Text;
using CrewTerm.App.UI.Input;
using CrewTerm.Terminal.Emulation;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Point = System.Drawing.Point;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 显示一个终端会话屏幕快照的视图：绘制单元格、转发键盘与鼠标输入、支持本地文本选择与滚动历史浏览。
/// </summary>
/// <remarks>
/// 视图本身不连接 daemon：工作台定期拉取快照后调用 <see cref="Update"/>，
/// 输入与复制通过 <see cref="SendInput"/>、<see cref="CopyRequested"/> 等回调交还工作台处理。
/// 鼠标事件在子程序开启鼠标追踪时转发给子程序；按住 Shift、正在浏览历史或子程序未开启追踪时在本地处理（选择、滚动、右键菜单）。
/// 选择文本或浏览历史期间画面冻结在某个快照上，新到的输出不会让选区或视图跳动。
/// 所有成员都应在 UI 线程使用。
/// </remarks>
public sealed class TerminalPane : View
{
    /// <summary>
    /// 选择文本或浏览历史期间冻结显示的快照；为 <see langword="null"/> 时显示最新的 <see cref="Snapshot"/>。
    /// </summary>
    private TerminalSnapshot? _frozenSnapshot;

    /// <summary>
    /// 选区的锚点（按下左键的位置）与当前终点，均为视口内的字符坐标（从 0 开始），可能前后颠倒，
    /// 使用前由 <see cref="SelectionRange"/> 排序。
    /// </summary>
    private Point _anchor, _selectionEnd;

    /// <summary>
    /// <c>_selecting</c>：正在拖动左键选择文本（已捕获鼠标）；
    /// <c>_hasSelection</c>：存在非空选区（锚点与终点不同）；
    /// <c>_applicationPressed</c>：一次按键按下已转发给子程序且已捕获鼠标，其后的拖动与释放也必须转发给子程序，
    /// 即使鼠标移出视口。
    /// </summary>
    private bool _selecting, _hasSelection, _applicationPressed;

    /// <summary>
    /// 向上浏览历史的行数；0 表示显示最新屏幕并跟随输出。
    /// </summary>
    private int _scrollOffset;

    /// <summary>
    /// <see cref="SessionId"/> 的存储字段。
    /// </summary>
    private string? _sessionId;

    /// <summary>
    /// 面板绑定的会话标识；为 <see langword="null"/> 时面板未绑定会话，显示启动提示。
    /// </summary>
    /// <remarks>
    /// 值发生变化时：释放转发给子程序的鼠标捕获，回到最新画面并清除选区，
    /// 并清空 <see cref="Snapshot"/>，直到下一次刷新拉取到新会话的画面。
    /// </remarks>
    public string? SessionId
    {
        get => _sessionId;
        set
        {
            if (_sessionId == value) return;
            if (_applicationPressed) { _applicationPressed = false; App?.Mouse.UngrabMouse(); }
            _sessionId = value;
            ScrollToBottom();
            Snapshot = null;
        }
    }
    /// <summary>
    /// 最近一次刷新得到的屏幕快照；尚未取得画面或会话不存在时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 只能通过 <see cref="Update"/> 设置；绑定的会话变化时被清空。
    /// 鼠标编码与应用光标键模式总是依据它，而不是冻结的快照。
    /// </remarks>
    public TerminalSnapshot? Snapshot { get; private set; }

    /// <summary>
    /// 把输入文本（按键编码、鼠标上报序列）发送给绑定会话的回调；为 <see langword="null"/> 时输入被丢弃。
    /// </summary>
    /// <remarks>
    /// 在 UI 线程同步调用，实现不应阻塞（工作台把它接到输入队列上）。
    /// </remarks>
    public Action<string>? SendInput { get; set; }

    /// <summary>
    /// 需要把文本复制到剪贴板时调用的回调，参数为选中的文本（非空）；由 <see cref="CopySelection"/> 触发。
    /// </summary>
    public Action<string>? CopyRequested { get; set; }

    /// <summary>
    /// 本地处理鼠标时按下右键调用的回调，参数为鼠标的屏幕坐标，用于在该位置弹出终端操作菜单。
    /// </summary>
    public Action<Point>? ContextMenuRequested { get; set; }

    /// <summary>
    /// 松开左键完成选择时是否自动复制选中文本；默认为 <see langword="true"/>。
    /// </summary>
    public bool CopyOnSelect { get; set; } = true;

    /// <summary>
    /// 当前是否存在非空选区。
    /// </summary>
    public bool HasSelection => _hasSelection;

    /// <summary>
    /// 向上浏览历史的行数；0 表示显示最新屏幕。
    /// </summary>
    public int ScrollOffset => _scrollOffset;

    /// <summary>
    /// 当前选中的文本；没有选区或没有可显示的快照时为空字符串。
    /// </summary>
    /// <remarks>
    /// 从当前显示的（可能是冻结的）快照中按行提取，行间以 <c>\n</c> 连接，每行去掉行尾空白。
    /// 选区起点落在宽字符的续位单元格上时向左扩展一格，使宽字符完整包含在内。
    /// </remarks>
    public string SelectedText
    {
        get
        {
            if (!_hasSelection || DisplaySnapshot is not { } snapshot) return "";
            var (start, end) = SelectionRange();
            var lines = new List<string>();
            for (var y = start.Y; y <= end.Y; y++)
            {
                var cells = VisibleLine(snapshot, y);
                var from = y == start.Y ? start.X : 0;
                var to = y == end.Y ? end.X : cells.Length - 1;
                if (cells[from].Width == 0 && from > 0) from--;
                var text = new StringBuilder();
                for (var x = from; x <= to; x++) text.Append(cells[x].Text);
                lines.Add(text.ToString().TrimEnd());
            }
            return string.Join('\n', lines);
        }
    }
    /// <summary>
    /// 当前用于绘制与选择的快照：冻结的快照优先，否则为最新的 <see cref="Snapshot"/>；两者都没有时为 <see langword="null"/>。
    /// </summary>
    private TerminalSnapshot? DisplaySnapshot => _frozenSnapshot ?? Snapshot;

    /// <summary>
    /// 创建填满父视图、可获得焦点并追踪鼠标位置的终端面板。
    /// </summary>
    /// <remarks>
    /// 获得焦点时的按键由 <see cref="TerminalInput.Encode"/> 编码（依据快照的应用光标键模式），
    /// 编码成功则回到最新画面并通过 <see cref="SendInput"/> 发送；无法编码的按键不处理，留给工作台的全局快捷键。
    /// </remarks>
    public TerminalPane()
    {
        CanFocus = true;
        MousePositionTracking = true;
        Width = Dim.Fill(); Height = Dim.Fill();
        SetScheme(new Scheme { Normal = new(new Color(0xd8dee9), new Color(0x10151d)) });
        KeyDown += (_, key) =>
        {
            var text = TerminalInput.Encode(key, Snapshot?.ApplicationCursorKeys ?? false);
            if (text is null) return;
            key.Handled = true;
            ScrollToBottom();
            SendInput?.Invoke(text);
        };
        MouseEvent += (_, mouse) => HandleMouse(mouse);
    }

    /// <summary>
    /// 处理一个鼠标事件：转发给子程序，或在本地完成选择、滚动与右键菜单。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 视口外的事件被忽略，除非正在拖动选择或已把按下事件转发给子程序（此时鼠标已被捕获，拖到视口外也继续跟踪）。
    /// Terminal.Gui 合成的单击、双击、三击事件被吞掉；任何按键按下都会让面板获得焦点。
    /// </description></item>
    /// <item><description>
    /// 正在选择时，或没有转发中的按下且满足“按住 Shift、正在浏览历史、子程序未开启 1000/1002/1003 鼠标追踪”之一时，
    /// 事件在本地处理；否则转发给子程序。按住 Shift 强制本地选择与 xterm 的习惯一致。
    /// </description></item>
    /// <item><description>
    /// 转发时：已捕获鼠标的事件坐标被夹到可见区域内（会改写 <paramref name="mouse"/> 的位置），
    /// 按下时捕获鼠标，释放时解除捕获。
    /// </description></item>
    /// <item><description>
    /// 本地处理时：右键按下请求上下文菜单；垂直滚轮每格滚动 3 行历史，水平滚轮被吞掉；
    /// 左键按下冻结当前画面并开始选择，拖动更新终点，松开时结束选择、解除捕获，并在 <see cref="CopyOnSelect"/> 时复制。
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="mouse">Terminal.Gui 鼠标事件，位置为视口内的字符坐标；处理过的事件会被标记为已处理。</param>
    private void HandleMouse(Mouse mouse)
    {
        if (mouse.Position is not { } position || Viewport.Width < 1 || Viewport.Height < 1) return;
        if (!_selecting && !_applicationPressed && (position.X < 0 || position.Y < 0 || position.X >= Viewport.Width || position.Y >= Viewport.Height)) return;
        if (mouse.IsSingleDoubleOrTripleClicked) { mouse.Handled = true; return; }
        var flags = mouse.Flags;
        if (mouse.IsPressed && !flags.HasFlag(MouseFlags.PositionReport)) { SetFocus(); mouse.Handled = true; }
        var local = _selecting || !_applicationPressed && (flags.HasFlag(MouseFlags.Shift) || _scrollOffset > 0 || Snapshot?.MouseTracking is not (1000 or 1002 or 1003));
        if (local && flags.HasFlag(MouseFlags.RightButtonPressed) && !flags.HasFlag(MouseFlags.PositionReport))
        {
            mouse.Handled = true;
            ContextMenuRequested?.Invoke(mouse.ScreenPosition);
            return;
        }
        if (DisplaySnapshot is not { } snapshot) return;
        if (!local)
        {
            if (_applicationPressed) mouse.Position = new(Math.Clamp(position.X, 0, Math.Min(Viewport.Width, snapshot.Columns) - 1),
                Math.Clamp(position.Y, 0, Math.Min(Viewport.Height, snapshot.Rows) - 1));
            if (SessionId is not null && TerminalMouse.Encode(mouse, Snapshot) is { } text)
            {
                if (mouse.IsPressed && !flags.HasFlag(MouseFlags.PositionReport))
                { _applicationPressed = true; App?.Mouse.GrabMouse(this); }
                mouse.Handled = true;
                SendInput?.Invoke(text);
            }
            if (mouse.IsReleased) { _applicationPressed = false; App?.Mouse.UngrabMouse(); }
            return;
        }
        if (flags.HasFlag(MouseFlags.WheeledUp) || flags.HasFlag(MouseFlags.WheeledDown))
        {
            if (!flags.HasFlag(MouseFlags.WheeledLeft) && !flags.HasFlag(MouseFlags.WheeledRight))
                ScrollHistory(flags.HasFlag(MouseFlags.WheeledUp) ? 3 : -3);
            mouse.Handled = true;
            return;
        }
        position = new(Math.Clamp(position.X, 0, Math.Min(Viewport.Width, snapshot.Columns) - 1),
            Math.Clamp(position.Y, 0, Math.Min(Viewport.Height, snapshot.Rows) - 1));
        if (flags.HasFlag(MouseFlags.LeftButtonPressed) && !flags.HasFlag(MouseFlags.PositionReport))
        {
            ClearSelection();
            _frozenSnapshot ??= Snapshot;
            _anchor = _selectionEnd = position;
            _selecting = true;
            App?.Mouse.GrabMouse(this);
            mouse.Handled = true;
        }
        else if (_selecting && (flags.HasFlag(MouseFlags.PositionReport) || flags.HasFlag(MouseFlags.LeftButtonReleased)))
        {
            _selectionEnd = position;
            _hasSelection = _selectionEnd != _anchor;
            mouse.Handled = true;
            if (flags.HasFlag(MouseFlags.LeftButtonReleased))
            {
                _selecting = false;
                App?.Mouse.UngrabMouse();
                if (!_hasSelection && _scrollOffset == 0) _frozenSnapshot = null;
                if (CopyOnSelect) CopySelection();
            }
        }
        SetNeedsDraw();
    }

    /// <summary>
    /// 清除选区并结束正在进行的拖动选择（解除鼠标捕获），然后请求重绘。
    /// </summary>
    /// <remarks>
    /// 没有在浏览历史时同时解冻画面，恢复显示最新快照；浏览历史期间画面保持冻结。
    /// </remarks>
    public void ClearSelection()
    {
        if (_selecting) App?.Mouse.UngrabMouse();
        _hasSelection = _selecting = false;
        if (_scrollOffset == 0) _frozenSnapshot = null;
        SetNeedsDraw();
    }

    /// <summary>
    /// 选中文本非空时通过 <see cref="CopyRequested"/> 交给工作台复制；否则什么也不做。
    /// </summary>
    public void CopySelection()
    {
        if (SelectedText is { Length: > 0 } text) CopyRequested?.Invoke(text);
    }

    /// <summary>
    /// 滚动浏览历史：正值向更旧的行滚动，负值向最新画面滚动。输出继续到来时，正在查看的快照保持不变。
    /// </summary>
    /// <remarks>
    /// 滚动会清除选区并冻结当前显示的快照；偏移限制在 0 到历史行数之间，
    /// 备用屏幕（全屏程序）没有滚动历史，偏移恒为 0。偏移回到 0 时解冻并恢复跟随输出。
    /// 没有可显示的快照时什么也不做。
    /// </remarks>
    /// <param name="lines">滚动的行数，正数向上（更旧）、负数向下。</param>
    public void ScrollHistory(int lines)
    {
        if (DisplaySnapshot is not { } snapshot) return;
        ClearSelection();
        _frozenSnapshot = snapshot;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, snapshot.AlternateScreen ? 0 : snapshot.History.Length);
        if (_scrollOffset == 0) _frozenSnapshot = null;
        SetNeedsDraw();
    }

    /// <summary>
    /// 回到最新画面：滚动偏移归零并清除选区（同时解冻画面、恢复跟随输出）。
    /// </summary>
    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        ClearSelection();
    }

    /// <summary>
    /// 按“先行后列”的顺序排列选区两端。
    /// </summary>
    /// <returns>选区的起点与终点（均包含在选区内），起点不晚于终点。</returns>
    private (Point Start, Point End) SelectionRange() => _anchor.Y < _selectionEnd.Y ||
        _anchor.Y == _selectionEnd.Y && _anchor.X <= _selectionEnd.X ? (_anchor, _selectionEnd) : (_selectionEnd, _anchor);

    /// <summary>
    /// 取视口第 <paramref name="y"/> 行在当前滚动偏移下显示的单元格。
    /// </summary>
    /// <remarks>
    /// 落在屏幕部分的行直接返回快照中的单元格数组；落在历史部分的行（只保存了纯文本）
    /// 每次调用都会重放到一个临时的单行屏幕中重新生成单元格（默认颜色，宽字符宽度正确）。
    /// </remarks>
    /// <param name="snapshot">要读取的快照。</param>
    /// <param name="y">视口行号，从 0 开始，必须小于快照行数。</param>
    /// <returns>该行的单元格；历史行最多包含 <see cref="TerminalSnapshot.Columns"/> 个单元格。</returns>
    private TerminalCell[] VisibleLine(TerminalSnapshot snapshot, int y)
    {
        var row = snapshot.History.Length - _scrollOffset + y;
        if (row >= snapshot.History.Length) return snapshot.Cells[row - snapshot.History.Length];
        // 历史行保留其原始宽度（最多 500 个单元格）。如果重放到较窄的单行屏幕，
        // 行首部分会在我们裁剪之前就因自动换行而被滚走，所以先用 500 列重放再截取。
        var line = new TerminalScreen(500, 1);
        line.Feed(snapshot.History[row]);
        return line.Snapshot().Cells[0].Take(snapshot.Columns).ToArray();
    }

    /// <summary>
    /// 设置最新的屏幕快照并请求重绘。
    /// </summary>
    /// <remarks>
    /// 画面冻结期间（正在选择或浏览历史）仍显示冻结的快照，新快照要等解冻后才可见；
    /// 但鼠标编码与光标键模式立即依据新快照。
    /// </remarks>
    /// <param name="snapshot">新快照；为 <see langword="null"/> 时表示没有画面（会话不存在或尚未取得）。</param>
    public void Update(TerminalSnapshot? snapshot)
    {
        Snapshot = snapshot;
        SetNeedsDraw();
    }

    /// <summary>
    /// 把终端单元格的颜色与样式转换为 Terminal.Gui 绘制属性。
    /// </summary>
    /// <remarks>
    /// 粗体、暗淡、斜体、下划线、闪烁、删除线映射为对应的 <see cref="TextStyle"/>；
    /// 隐藏文字（SGR 8）的前景色改为背景色，使其不可见。
    /// </remarks>
    /// <param name="cell">要转换的单元格，颜色为 24 位 RGB。</param>
    /// <returns>对应的绘制属性。</returns>
    public static Attribute GetCellAttribute(TerminalCell cell)
    {
        var style = TextStyle.None;
        if (cell.Bold) style |= TextStyle.Bold;
        if (cell.Faint) style |= TextStyle.Faint;
        if (cell.Italic) style |= TextStyle.Italic;
        if (cell.Underline) style |= TextStyle.Underline;
        if (cell.Blink) style |= TextStyle.Blink;
        if (cell.Strikethrough) style |= TextStyle.Strikethrough;
        return new(new Color(cell.Hidden ? cell.Background : cell.Foreground), new Color(cell.Background), style);
    }

    /// <summary>
    /// 绘制面板内容与光标。
    /// </summary>
    /// <remarks>
    /// 没有可显示的快照时绘制启动提示。否则在视口与快照尺寸的交集内逐格绘制：
    /// 跳过宽字符的续位单元格，选区以白字蓝底高亮，隐藏文字绘制为等宽的空格。
    /// 只有面板获得焦点、子程序光标可见且画面未冻结时，才在子程序光标位置显示闪烁竖线光标，否则隐藏光标。
    /// </remarks>
    /// <param name="context">绘制上下文（未使用）。</param>
    /// <returns>始终为 <see langword="true"/>，表示内容已由本视图绘制完毕。</returns>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var snapshot = DisplaySnapshot;
        if (snapshot is null)
        {
            // 空面板的提示：说明这里能做什么，以及侧栏与前缀键的用法。
            SetAttribute(new Attribute(new Color(0xc6d1df), new Color(0x10151d)));
            Move(2, 1); AddStr("This pane is empty.");
            SetAttribute(new Attribute(new Color(0x7f849c), new Color(0x10151d)));
            Move(2, 3); AddStr("F3            start an agent or a shell here");
            Move(2, 4); AddStr("click an agent in the sidebar to show it here");
            Move(2, 5); AddStr("click a file under files to view it below");
            Move(2, 6); AddStr("ctrl+b v / -  split right / down");
            Move(2, 7); AddStr("ctrl+b c      new tab");
            return true;
        }
        var (start, end) = SelectionRange();
        for (var y = 0; y < Math.Min(Viewport.Height, snapshot.Rows); y++)
        {
            var cells = VisibleLine(snapshot, y);
            for (var x = 0; x < Math.Min(Viewport.Width, snapshot.Columns); x++)
            {
                var cell = cells[x];
                if (cell.Width == 0) continue;
                var selected = _hasSelection && y >= start.Y && y <= end.Y &&
                    (y != start.Y || x + cell.Width - 1 >= start.X) && (y != end.Y || x <= end.X);
                var attribute = GetCellAttribute(selected ? cell with { Foreground = 0xffffff, Background = 0x315c82 } : cell);
                SetAttribute(attribute); Move(x, y); AddStr(cell.Hidden ? new string(' ', cell.Width) : cell.Text);
            }
        }
        if (HasFocus && snapshot.CursorVisible && _frozenSnapshot is null)
        {
            var position = ViewportToScreen().Location;
            Cursor = new() { Position = new(position.X + snapshot.CursorColumn, position.Y + snapshot.CursorRow), Style = CursorStyle.BlinkingBar };
        }
        else Cursor = new() { Position = null };
        return true;
    }
}
