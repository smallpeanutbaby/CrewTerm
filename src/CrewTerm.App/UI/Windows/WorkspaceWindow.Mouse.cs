using CrewTerm.App.UI.Controls;
using CrewTerm.Core.Editing;
using CrewTerm.Core.Layouts;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：终端面板的回调——输入、复制与右键菜单，以及复制选区、选择会话、打开选中的文件引用等面板操作。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 为新建的终端面板连接回调：输入进入有序发送队列，复制写入剪贴板，右键弹出终端操作菜单。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="TerminalLayoutView.PaneCreated"/> 在 UI 线程调用。右键菜单先聚焦该面板，
    /// 使菜单中的命令（复制、粘贴、分屏、关闭等）都作用于被点击的面板；回调中的异常由 <see cref="Guard"/> 报告。
    /// </remarks>
    /// <param name="pane">新建的面板。</param>
    private void ConfigureTerminalPane(TerminalPane pane)
    {
        pane.SendInput = text => QueueInput(pane, text);
        pane.CopyRequested = text => Guard(() =>
        {
            if (application.Clipboard is null) throw new InvalidOperationException("Clipboard unavailable in this terminal");
            application.Clipboard.SetClipboardData(text);
            status.Text = "Selected terminal text copied.";
        });
        pane.ContextMenuRequested = _ => Guard(() =>
        {
            pane.SetFocus();
            var commands = new PaletteCommand[]
            {
                new("copy.selection", "Copy selected text", CopySelection),
                new("copy.screen", "Copy visible screen", CopyScreen),
                new("paste", "Paste clipboard", Paste),
                new("history", "View / search history", ShowScrollback),
                new("choose", "Choose existing session", ChooseTerminalSession),
                new("new", "Start new Agent or shell", NewAgent),
                new("right", "Split right", () => SplitPane(SplitDirection.Right)),
                new("down", "Split down", () => SplitPane(SplitDirection.Down)),
                new("left", "Split left", () => SplitPane(SplitDirection.Left)),
                new("up", "Split up", () => SplitPane(SplitDirection.Up)),
                new("file", "Open selected file reference", OpenSelectedReference),
                new("close", "Close pane (keep session running)", ClosePane)
            };
            var index = Dialogs.Choose(application, "TERMINAL ACTIONS", commands.Select(c => c.Label).ToArray());
            if (index >= 0) commands[index].Execute();
        });
    }

    /// <summary>
    /// 复制活动终端的选中文本（Ctrl+Shift+C）；没有选区时改为复制整个可见屏幕。
    /// </summary>
    private void CopySelection()
    {
        if (ActiveTerminal.HasSelection) ActiveTerminal.CopySelection();
        else CopyScreen();
    }

    /// <summary>
    /// 从全部会话中选择一个并显示到终端区（已显示则聚焦所在面板，否则替换活动面板的会话）。
    /// </summary>
    private void ChooseTerminalSession()
    {
        var index = Dialogs.Choose(application, "Choose terminal session", sessions.Select(s => $"{s.Name}  ·  {ActivityLabel(s)}  ·  {s.Id}").ToArray());
        if (index >= 0) FocusSession(sessions[index]);
    }

    /// <summary>
    /// 解析活动终端选中文本（去掉首尾空白）中的第一个文件位置并在编辑器中打开，跳到对应行。
    /// </summary>
    /// <remarks>
    /// 支持 <c>path:line[:column]</c> 与 <c>path(line[,column])</c> 两种写法，相对路径以项目根目录为基准；列号不使用。
    /// 注意：解析器要求必须带行号，只选中路径本身时同样视为无法识别。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目，或选中文本中没有可识别的带行号文件位置。</exception>
    /// <exception cref="UnauthorizedAccessException">解析出的文件位于项目之外或经过符号链接。</exception>
    private void OpenSelectedReference()
    {
        var selected = ActiveTerminal.SelectedText.Trim().Trim('"', '\'');
        if (selected.Length == 0) throw new InvalidOperationException("Select a file path, optionally followed by :line:column, first");
        // 与“打开路径”一致：带行号时跳到该行，否则把所选文本当作项目内的文件路径打开。
        var location = FileReferenceParser.Parse(selected, Root);
        if (location is not null) OpenFile(Path.GetRelativePath(Root, location.Path), location.Line);
        else OpenFile(Path.IsPathRooted(selected) ? Path.GetRelativePath(Root, selected) : selected);
    }
}
