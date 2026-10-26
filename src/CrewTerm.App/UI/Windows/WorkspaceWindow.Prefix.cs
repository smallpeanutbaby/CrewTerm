using CrewTerm.Core.Layouts;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// herdr / tmux 风格的前缀键：先按 <c>Ctrl+b</c>，再按一个键执行工作台操作。
/// </summary>
/// <remarks>
/// <para>前缀之后的键：<c>N</c> 新建空间，<c>c</c> 新建标签，<c>n</c> / <c>p</c> 下一个 / 上一个标签，<c>1</c>–<c>9</c> 跳到第几个标签，
/// <c>T</c> 重命名标签，<c>X</c> 关闭标签，<c>v</c> 向右分屏，<c>-</c> 向下分屏，<c>o</c> 下一个面板，<c>x</c> 关闭面板，
/// <c>w</c> 聚焦空间列表，<c>g</c> 聚焦 Agent 列表，<c>b</c> 显示或隐藏侧栏，<c>q</c> 离开界面（会话继续运行），<c>?</c> 帮助，
/// Esc 取消；再按一次 <c>Ctrl+b</c> 把 <c>Ctrl+b</c> 本身发给获得焦点的终端。</para>
/// <para>原有的 F1–F10 快捷键全部保留，前缀键只是另一种方式。</para>
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 前缀键之后各键的说明，显示在状态栏中。
    /// </summary>
    private const string PrefixHint =
        "ctrl+b  ·  N space  c tab  n/p next/prev  1-9 tab  v/- split  o next pane  x close  w spaces  g agents  b sidebar  q detach  ? help";

    /// <summary>
    /// 是否已按下前缀键、正在等待下一个键。
    /// </summary>
    private bool prefixPending;

    /// <summary>
    /// 处理前缀键与紧随其后的一个键。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <returns>按键属于前缀序列（已处理，不应再交给其他处理）时为 <see langword="true"/>。</returns>
    private bool HandlePrefix(Key key)
    {
        if (!prefixPending)
        {
            if (key != Key.B.WithCtrl) return false;
            prefixPending = true;
            status.Text = PrefixHint;
            return true;
        }
        prefixPending = false;
        status.Text = "";
        if (key == Key.Esc) return true;
        if (key == Key.B.WithCtrl)
        {
            // 连按两次前缀：把 Ctrl+b 本身发给终端（例如 readline 的向左移动光标）。
            if (terminalLayout.Panes.FirstOrDefault(p => p.HasFocus) is { } terminal) QueueInput(terminal, "\u0002");
            return true;
        }
        var action = PrefixAction(key.AsRune.Value);
        if (action is null) status.Text = "Unknown key after ctrl+b. " + PrefixHint;
        else Guard(action);
        return true;
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
        'w' => () => spaceSection.SetFocus(),
        'g' => () => agentSection.SetFocus(),
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
