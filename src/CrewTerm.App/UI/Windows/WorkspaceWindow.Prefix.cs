using CrewTerm.App.UI.Input;
using CrewTerm.Core.Layouts;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// herdr / tmux 风格的前缀键：先按 <c>Ctrl+b</c>，再在 2 秒内按一个键执行工作台操作。
/// </summary>
/// <remarks>
/// <para>前缀之后的键：<c>N</c> 新建空间，<c>c</c> 新建标签，<c>n</c> / <c>p</c> 下一个 / 上一个标签，<c>1</c>–<c>9</c> 跳到第几个标签，
/// <c>T</c> 重命名标签，<c>X</c> 关闭标签，<c>v</c> 向右分屏，<c>-</c> 向下分屏，<c>o</c> 下一个面板，<c>x</c> 关闭面板，
/// <c>w</c> 聚焦空间列表，<c>g</c> 聚焦 Agent 列表，<c>f</c> 聚焦文件列表，<c>e</c> 显示或隐藏代码查看器，
/// <c>b</c> 显示或隐藏侧栏，<c>q</c> 离开界面（会话继续运行），<c>?</c> 帮助，
/// Esc 取消；再按一次 <c>Ctrl+b</c> 把 <c>Ctrl+b</c> 本身发给获得焦点的终端。超时、修饰键与重复按键的规则见 <see cref="PrefixKeyState"/>。</para>
/// <para>原有的 F1–F10 快捷键全部保留，前缀键只是另一种方式。</para>
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 前缀键之后各键的说明，显示在状态栏中；不超过 116 列，120 列宽的终端中也能完整显示。
    /// </summary>
    private const string PrefixHint =
        "^b  N space  c/n/p/1-9 tab  v/- split  o pane  x close  w/g/f spaces/agents/files  e code  b sidebar  q detach  ?";

    /// <summary>
    /// 前缀键的状态。
    /// </summary>
    private readonly PrefixKeyState prefix = new();

    /// <summary>
    /// 处理前缀键与紧随其后的一个键。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <returns>按键属于前缀序列（已处理，不应再交给其他处理）时为 <see langword="true"/>。</returns>
    private bool HandlePrefix(Key key)
    {
        var (outcome, character) = prefix.Process(key, DateTime.UtcNow);
        switch (outcome)
        {
            case PrefixKeyOutcome.NotPrefix:
                return false;
            case PrefixKeyOutcome.Started:
                status.Text = PrefixHint;
                return true;
            case PrefixKeyOutcome.Ignored:
                return true;
            case PrefixKeyOutcome.SendPrefix:
                status.Text = "";
                // 连按两次前缀：把 Ctrl+b 本身发给终端（例如 readline 的向左移动光标）。
                if (terminalLayout.Panes.FirstOrDefault(p => p.HasFocus) is { } terminal) QueueInput(terminal, "\u0002");
                return true;
            case PrefixKeyOutcome.Command when PrefixAction(character) is { } action:
                status.Text = "";
                Guard(action);
                return true;
            case PrefixKeyOutcome.Cancelled:
                status.Text = "";
                return true;
            default:
                status.Text = "Unknown key after ctrl+b. " + PrefixHint;
                return true;
        }
    }

    /// <summary>
    /// 应用级鼠标处理：按下任何鼠标按键都取消等待中的前缀（用户已改用鼠标，之后键入的字符应当交给终端）。
    /// </summary>
    /// <param name="sender">事件来源（未使用）。</param>
    /// <param name="mouse">鼠标事件；只移动鼠标不取消前缀。</param>
    private void CancelPrefixOnMouse(object? sender, Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.PositionReport) || !prefix.IsPending(DateTime.UtcNow)) return;
        prefix.Cancel();
        status.Text = "";
    }

    /// <summary>
    /// 前缀键之后某个字符对应的操作。
    /// </summary>
    /// <param name="character">按键对应的字符（区分大小写）。</param>
    /// <returns>对应的操作；没有对应操作时为 <see langword="null"/>。</returns>
    private Action? PrefixAction(int character) => character switch
    {
        'N' => AddProject,
        'c' => NewTab,
        'n' => () => SwitchTab(1),
        'p' => () => SwitchTab(-1),
        >= '1' and <= '9' => () => SelectTabByNumber(character - '0'),
        'T' => RenameTab,
        'X' => CloseTab,
        'v' => () => SplitPane(SplitDirection.Right),
        '-' => () => SplitPane(SplitDirection.Down),
        'o' => NextPane,
        'x' => ClosePane,
        'w' => () => FocusSidebar(spaceSection),
        'g' => () => FocusSidebar(agentSection),
        'f' => () => FocusSidebar(fileSection),
        'e' => ToggleEditor,
        'b' => ToggleSidebar,
        'q' => Detach,
        '?' => ShowHelp,
        _ => null
    };

    /// <summary>
    /// 跳到当前项目的第几个标签页。
    /// </summary>
    /// <param name="number">从 1 开始的标签序号；超出范围时什么也不做。</param>
    private void SelectTabByNumber(int number)
    {
        var tabs = terminalLayout.LayoutModel.Tabs;
        if (number <= tabs.Count) terminalLayout.SelectTab(tabs[number - 1].Id);
    }
}
