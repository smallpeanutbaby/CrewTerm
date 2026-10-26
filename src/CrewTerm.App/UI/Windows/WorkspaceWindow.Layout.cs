using CrewTerm.App.UI.Controls;
using CrewTerm.Core.IO;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Settings;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：终端布局——按项目加载与保存布局、分屏、关闭面板、切换面板，以及标签页的新建、重命名、关闭与切换。
/// 这些操作只改变布局，面板或标签页中的会话始终继续在 daemon 中运行。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 加载当前项目的终端布局并显示；该项目还没有布局时新建一个并登记到设置中。
    /// </summary>
    /// <remarks>
    /// 布局按 <see cref="WorkspaceSettings.LastProjectId"/> 保存（没有项目时用键 <c>_none</c>）。
    /// 新布局包含一个名为 “Terminal” 的标签页；若设置中还没有任何布局且 <see cref="WorkspaceSettings.SplitTerminals"/>
    /// 为 <see langword="true"/>（双终端分屏开关），则额外向右分出一个空面板，并让第一个面板保持活动。
    /// </remarks>
    /// <param name="initialSession">新建布局时第一个面板要绑定的会话标识；为 <see langword="null"/> 时面板为空，由刷新自动分配。已有布局时忽略。</param>
    private void LoadProjectLayout(string? initialSession = null)
    {
        var key = settings.LastProjectId ?? "_none";
        if (!settings.ProjectLayouts.TryGetValue(key, out var layout))
        {
            layout = new TerminalLayout();
            var tab = layout.AddTab("Terminal", initialSession);
            if (settings.ProjectLayouts.Count == 0 && settings.SplitTerminals)
            {
                layout.Split(tab.Root.Id, SplitDirection.Right);
                tab.ActivePaneId = tab.Panes.First().Id;
            }
            settings.ProjectLayouts[key] = layout;
        }
        terminalLayout.ShowLayout(layout);
    }

    /// <summary>
    /// 把当前标签页的面板与会话绑定写回布局模型，并按面板数更新 <see cref="WorkspaceSettings.SplitTerminals"/>；只修改内存中的设置，不写盘。
    /// </summary>
    private void SaveCurrentLayout()
    {
        terminalLayout.SaveBindings();
        settings.SplitTerminals = terminalLayout.Panes.Count > 1;
    }

    /// <summary>
    /// 把活动面板沿指定方向一分为二，新面板获得焦点。
    /// </summary>
    /// <remarks>
    /// 新面板优先绑定一个当前标签页尚未显示、工作目录属于当前项目的会话；没有时为空面板。
    /// 分屏后立即发起一次刷新以显示其画面。
    /// </remarks>
    /// <param name="direction">新面板相对于活动面板的位置（左、右、上、下）。</param>
    /// <exception cref="InvalidOperationException">当前标签页已有 16 个面板。</exception>
    private void SplitPane(SplitDirection direction)
    {
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        var layout = terminalLayout.LayoutModel;
        var pane = layout.ActiveTab.Panes.First(p => p.Id == layout.ActiveTab.ActivePaneId);
        var used = terminalLayout.Panes.Select(p => p.SessionId).ToHashSet();
        var available = sessions.FirstOrDefault(s => !used.Contains(s.Id) &&
            FileSystemPath.AreSame(s.WorkingDirectory, projectRoot));
        layout.Split(pane.Id, direction, available?.Id);
        terminalLayout.Rebuild(); terminalLayout.ActivePane.SetFocus();
        settings.SplitTerminals = true;
        BeginRefresh();
    }

    /// <summary>
    /// 从布局中移除活动面板，其会话继续运行；关闭的是标签页中唯一的面板时整个标签页随之关闭。
    /// </summary>
    private void ClosePane()
    {
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        var layout = terminalLayout.LayoutModel;
        layout.ClosePane(layout.ActiveTab.ActivePaneId!);
        terminalLayout.Rebuild(); terminalLayout.ActivePane.SetFocus();
        status.Text = "Pane closed; its session continues running.";
    }

    /// <summary>
    /// 按视觉顺序把焦点移到下一个面板，最后一个之后回到第一个。
    /// </summary>
    private void NextPane()
    {
        var panes = terminalLayout.Panes;
        var index = panes.ToList().IndexOf(ActiveTerminal);
        panes[(index + 1) % panes.Count].SetFocus();
    }

    /// <summary>
    /// 询问名称（默认为 “Terminal N”）并新建一个只有空面板的标签页，切换过去并聚焦；取消时什么也不做。
    /// </summary>
    /// <exception cref="ArgumentException">名称不是 1–60 个可打印字符。</exception>
    /// <exception cref="InvalidOperationException">当前项目已有 16 个标签页。</exception>
    private void NewTab()
    {
        var name = Dialogs.Prompt(application, "New tab", "New tab name", "Terminal " + (terminalLayout.LayoutModel.Tabs.Count + 1));
        if (name is null) return;
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        terminalLayout.LayoutModel.AddTab(name);
        terminalLayout.Rebuild(); terminalLayout.ActivePane.SetFocus();
        settings.SplitTerminals = false;
    }

    /// <summary>
    /// 询问新名称并重命名活动标签页；取消时什么也不做。
    /// </summary>
    /// <exception cref="ArgumentException">名称不是 1–60 个可打印字符。</exception>
    private void RenameTab()
    {
        var tab = terminalLayout.LayoutModel.ActiveTab;
        var name = Dialogs.Prompt(application, "Rename tab", "Tab name", tab.Name);
        if (name is null) return;
        terminalLayout.LayoutModel.RenameTab(tab.Id, name); terminalLayout.Rebuild(); terminalLayout.ActivePane.SetFocus();
    }

    /// <summary>
    /// 关闭活动标签页，其中的会话继续运行；关闭最后一个标签页时自动新建一个空的 “Terminal” 标签页。
    /// </summary>
    private void CloseTab()
    {
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        var layout = terminalLayout.LayoutModel;
        layout.CloseTab(layout.ActiveTab.Id);
        terminalLayout.Rebuild(); terminalLayout.ActivePane.SetFocus();
        status.Text = "Tab closed; its sessions continue running.";
    }

    /// <summary>
    /// 循环切换到相邻的标签页。
    /// </summary>
    /// <param name="direction">偏移量：1 为下一个，-1 为上一个；越过两端时循环。</param>
    private void SwitchTab(int direction)
    {
        var layout = terminalLayout.LayoutModel;
        var index = layout.Tabs.FindIndex(t => t.Id == layout.ActiveTabId);
        refreshGate.SelectionChanged();
        terminalLayout.SelectTab(layout.Tabs[(index + direction + layout.Tabs.Count) % layout.Tabs.Count].Id);
    }

    /// <summary>
    /// 从列表（显示标签名与面板数）中选择一个标签页并切换过去。
    /// </summary>
    private void ChooseTab()
    {
        var layout = terminalLayout.LayoutModel;
        var choice = Dialogs.Choose(application, "Choose tab", layout.Tabs.Select(t => $"{t.Name}  ·  {t.Panes.Count()} panes").ToArray());
        if (choice < 0) return;
        refreshGate.SelectionChanged(); terminalLayout.SelectTab(layout.Tabs[choice].Id);
    }
}
