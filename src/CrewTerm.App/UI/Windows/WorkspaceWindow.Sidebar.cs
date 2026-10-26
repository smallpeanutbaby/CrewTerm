using CrewTerm.App.UI.Controls;
using CrewTerm.Core.IO;
using CrewTerm.Workspace.Git;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// 工作台的 herdr 风格侧栏：<c>spaces</c> 分区列出项目（状态汇总、名称、Git 分支），<c>agents</c> 分区列出全部会话（状态、名称、“状态 · 程序”）。
/// </summary>
/// <remarks>
/// 单击空间行切换项目，单击 Agent 行跳到它所在的面板（必要时先切换项目），右键打开对应的菜单，单击 <c>+ new</c> 新建。
/// 行内容在每次会话刷新、项目切换后由 <see cref="RefreshSidebar"/> 重建。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 侧栏宽度（列）。
    /// </summary>
    private const int SidebarWidth = 34;

    /// <summary>
    /// <c>spaces</c> 分区最多显示的项目行数；更多项目时在分区内滚动。
    /// </summary>
    private const int MaxSpaceRows = 10;

    /// <summary>
    /// 重新读取各项目 Git 分支的最短间隔。
    /// </summary>
    private static readonly TimeSpan BranchRefreshInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 左上方的 <c>spaces</c> 分区，各行与 <see cref="projects"/> 的项目一一对应。
    /// </summary>
    private readonly SidebarSection spaceSection = new("spaces", "add a project  ·  F2");

    /// <summary>
    /// 左下方的 <c>agents</c> 分区，各行与 <see cref="sessions"/> 一一对应（等待决定的排在最前）。
    /// </summary>
    private readonly SidebarSection agentSection = new("agents", "start an agent  ·  F3");

    /// <summary>
    /// 各项目路径最近一次读取到的 Git 分支及读取时间；按平台的路径大小写规则比较键。
    /// </summary>
    private readonly Dictionary<string, (string? Branch, DateTime ReadAt)> branchCache = new(FileSystemPath.Comparer);

    /// <summary>
    /// <see cref="agentSection"/> 各行对应的会话标识，用于在列表重建后保持选中同一个会话。
    /// </summary>
    private string[] agentRowIds = [];

    /// <summary>
    /// 侧栏是否显示（<c>Ctrl+b b</c> 切换）；隐藏时终端区占满整个宽度。
    /// </summary>
    private bool sidebarVisible = true;

    /// <summary>
    /// 显示或隐藏侧栏，并重新排列各区域。
    /// </summary>
    private void ToggleSidebar()
    {
        sidebarVisible = !sidebarVisible;
        ArrangePanels();
    }

    /// <summary>
    /// 连接侧栏两个分区的单击、右键与新建回调。在构造窗口时调用一次。
    /// </summary>
    private void ConfigureSidebar()
    {
        spaceSection.RowActivated = index => Guard(() =>
        {
            if (index < projects.Projects.Count) SwitchProject(projects.Projects[index]);
        });
        spaceSection.CreateRequested = () => Guard(AddProject);
        spaceSection.ContextRequested = index => Guard(() => SpaceMenu(index));
        agentSection.RowActivated = index => Guard(() =>
        {
            if (index < sessions.Length) FocusSession(sessions[index]);
        });
        agentSection.CreateRequested = () => Guard(NewAgent);
        agentSection.ContextRequested = index => Guard(() => AgentMenu(index));
    }

    /// <summary>
    /// 按当前项目、会话与活动面板重建侧栏两个分区的行。
    /// </summary>
    /// <remarks>
    /// <c>spaces</c>：行首符号为该项目中所有会话状态的汇总（等待决定优先），右侧为 Git 分支，当前项目以粗体高亮并被选中。
    /// <c>agents</c>：按 <see cref="sessions"/> 的顺序列出，右侧为“状态 · 程序”，活动面板正在显示的会话以粗体高亮；尽量保持原选择。
    /// </remarks>
    private void RefreshSidebar()
    {
        var items = projects.Projects;
        var current = -1;
        var spaceRows = new List<SidebarRow>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var project = items[index];
            var isCurrent = FileSystemPath.AreSame(project.Path, projectRoot);
            if (isCurrent) current = index;
            var badge = AgentStateStyle.RollUp(sessions.Where(s => FileSystemPath.IsWithin(project.Path, s.WorkingDirectory)));
            spaceRows.Add(new(badge.Glyph, badge.Color, project.Name, BranchOf(project.Path) ?? "", isCurrent));
        }
        spaceSection.SetRows(spaceRows, spaceSection.HasFocus ? null : Math.Max(0, current));
        // 分区高度随项目数变化（标题一行加项目行，没有项目时为占位提示一行），agents 分区紧随其后。
        spaceSection.Height = Math.Clamp(spaceRows.Count, 1, MaxSpaceRows) + 1;
        var selectedId = agentSection.SelectedIndex is var selected and >= 0 && selected < agentRowIds.Length ? agentRowIds[selected] : null;
        // 构造窗口时侧栏先于终端布局建立，此时还没有标签页，也就没有正在显示的会话。
        var shown = terminalLayout.LayoutModel.Tabs.Count == 0 ? null : ActiveTerminal.SessionId;
        agentRowIds = sessions.Select(s => s.Id).ToArray();
        agentSection.SetRows(sessions.Select(s =>
        {
            var badge = AgentStateStyle.For(s);
            return new SidebarRow(badge.Glyph, badge.Color, s.Name, AgentStateStyle.Describe(s), s.Id == shown);
        }).ToArray(), selectedId is null ? null : Math.Max(0, Array.IndexOf(agentRowIds, selectedId)));
    }

    /// <summary>
    /// 读取项目所在仓库的当前 Git 分支；同一项目在 <see cref="BranchRefreshInterval"/> 内只读取一次。
    /// </summary>
    /// <param name="path">项目路径。</param>
    /// <returns>分支名；不在仓库中时为 <see langword="null"/>。</returns>
    private string? BranchOf(string path)
    {
        var now = DateTime.UtcNow;
        if (branchCache.TryGetValue(path, out var cached) && now - cached.ReadAt < BranchRefreshInterval) return cached.Branch;
        var branch = GitBranch.Read(path);
        branchCache[path] = (branch, now);
        return branch;
    }

    /// <summary>
    /// 空间行的菜单：打开该项目、在其中启动 Agent，或从侧栏移除登记（磁盘上的文件夹与会话都不受影响）。
    /// </summary>
    /// <param name="index">空间行索引。</param>
    private void SpaceMenu(int index)
    {
        if (index >= projects.Projects.Count) return;
        var project = projects.Projects[index];
        var choice = Dialogs.Choose(application, project.Name + "  ·  " + project.Path, ["Open", "Start an agent here", "Remove from sidebar"]);
        if (choice is 0 or 1) SwitchProject(project);
        if (choice == 1 && FileSystemPath.AreSame(projectRoot, project.Path)) NewAgent();
        if (choice != 2 || !Dialogs.Confirm(application, "Remove space",
                $"Remove {project.Name} from the sidebar? The folder and its running agents are not affected.")) return;
        projects.Remove(project.Id); projects.Save(); RefreshProjects();
        status.Text = "Removed sidebar entry; folder remains on disk.";
    }

    /// <summary>
    /// Agent 行的菜单：显示该会话，或对它执行常用的协作操作（解释状态、发送消息、交接提示词、等待状态、标记已读、终止）。
    /// </summary>
    /// <remarks>
    /// 除“显示”外的操作都先把该会话显示到活动面板，再调用作用于活动面板会话的现有命令。
    /// </remarks>
    /// <param name="index">Agent 行索引。</param>
    private void AgentMenu(int index)
    {
        if (index >= sessions.Length) return;
        var session = sessions[index];
        var choice = Dialogs.Choose(application, session.Name + "  ·  " + AgentStateStyle.Describe(session),
            ["Show", "Explain status", "Send message…", "Handoff prompt…", "Wait for state…", "Mark notifications read", "Terminate…"]);
        if (choice < 0 || !FocusSession(session)) return;
        switch (choice)
        {
            case 1: ExplainAgent(); break;
            case 2: SendAgentMessage(false); break;
            case 3: SendAgentMessage(true); break;
            case 4: WaitForAgent(); break;
            case 5: MarkSelectedRead(); break;
            case 6: CloseSession(); break;
        }
    }
}
