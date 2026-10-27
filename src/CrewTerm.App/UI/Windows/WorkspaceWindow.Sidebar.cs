using CrewTerm.App.UI.Controls;
using CrewTerm.Core.IO;
using CrewTerm.App.Protocol;
using CrewTerm.Workspace.Git;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// 工作台的 herdr 风格侧栏：<c>spaces</c> 分区列出项目（状态汇总、名称、Git 分支），<c>agents</c> 分区列出全部会话（状态、名称、“状态 · 程序”），
/// 其下的 <c>files</c> 分区浏览当前项目的文件（见 <c>WorkspaceWindow.Files.cs</c>）。
/// </summary>
/// <remarks>
/// <para>单击空间行切换项目并把焦点交给终端，单击 Agent 行跳到它所在的面板（必要时先切换项目），右键打开对应的菜单，单击 <c>+ new</c> 新建，
/// 在侧栏中按 Esc 回到终端。前两个分区的行在每次会话刷新、项目切换后由 <see cref="RefreshSidebar"/> 重建。</para>
/// <para>各行以项目标识或会话标识为 <see cref="SidebarRow.Key"/>，单击与菜单按标识找回对象，列表在两次绘制之间重新排序也不会点错。</para>
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
    /// <c>agents</c> 分区最多显示的会话行数；更多会话时在分区内滚动。
    /// </summary>
    private const int MaxAgentRows = 8;

    /// <summary>
    /// 重新读取各项目 Git 分支的最短间隔。
    /// </summary>
    private static readonly TimeSpan BranchRefreshInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 左上方的 <c>spaces</c> 分区，每个项目一行，行的键为项目标识。
    /// </summary>
    private readonly SidebarSection spaceSection = new("spaces", "+ add a project  ·  F2");

    /// <summary>
    /// 中间的 <c>agents</c> 分区，按 <see cref="sessions"/> 的顺序每个会话一行（等待决定的排在最前），行的键为会话标识。
    /// </summary>
    private readonly SidebarSection agentSection = new("agents", "+ start an agent  ·  F3");

    /// <summary>
    /// 各项目路径最近一次读取到的 Git 分支及读取时间；只在 UI 线程访问，按平台的路径大小写规则比较键。
    /// </summary>
    private readonly Dictionary<string, (string? Branch, DateTime ReadAt)> branchCache = new(FileSystemPath.Comparer);

    /// <summary>
    /// 正在后台读取 Git 分支的项目路径；只在 UI 线程访问。同一路径同时只有一次读取，读取卡住（例如网络路径无响应）时不会堆积线程。
    /// </summary>
    private readonly HashSet<string> branchReads = new(FileSystemPath.Comparer);

    /// <summary>
    /// 侧栏是否显示（<c>Ctrl+b b</c> 切换）；隐藏时终端区占满整个宽度。
    /// </summary>
    private bool sidebarVisible = true;

    /// <summary>
    /// <c>spaces</c> 或 <c>agents</c> 分区的高度：标题一行加行数（没有行时为占位提示一行），不超过 <paramref name="maximum"/>，
    /// 也不超过侧栏可用高度的三分之一，保证下方的 <c>files</c> 分区始终有空间。
    /// </summary>
    /// <remarks>
    /// 作为 <c>Dim.Func</c> 在每次布局时求值，窗口大小变化时自动重新计算；行数变化导致高度变化时由 <see cref="RefreshSidebar"/> 请求重新布局。
    /// 不直接给 <c>Height</c> 赋值：Terminal.Gui 的 <c>Height</c> 赋值即使数值不变也会触发整屏清除重绘。
    /// </remarks>
    /// <param name="section">分区。</param>
    /// <param name="maximum">最多显示的行数。</param>
    /// <returns>分区高度（行），至少为 2。</returns>
    private int SectionHeight(SidebarSection section, int maximum)
    {
        // 可用高度扣除底部两行（状态栏与快捷键提示）与分区之间的两个空行。
        var share = Math.Max(1, (Viewport.Height - 4) / 3 - 1);
        return Math.Clamp(section.Rows.Count, 1, Math.Max(1, Math.Min(maximum, share))) + 1;
    }

    /// <summary>
    /// 显示或隐藏侧栏，并重新排列各区域；隐藏时若焦点在侧栏中，把焦点交给活动终端。
    /// </summary>
    private void ToggleSidebar()
    {
        var hadFocus = spaceSection.HasFocus || agentSection.HasFocus || fileSection.HasFocus;
        sidebarVisible = !sidebarVisible;
        ArrangePanels();
        if (!sidebarVisible && hadFocus) FocusActiveTerminal();
    }

    /// <summary>
    /// 聚焦侧栏的一个分区；侧栏隐藏时先显示（隐藏的视图不能获得焦点）。
    /// </summary>
    /// <param name="section">要聚焦的分区。</param>
    private void FocusSidebar(SidebarSection section)
    {
        if (!sidebarVisible) ToggleSidebar();
        section.SetFocus();
    }

    /// <summary>
    /// 把焦点交给活动终端面板；终端布局尚未建立时什么也不做。
    /// </summary>
    private void FocusActiveTerminal()
    {
        if (terminalLayout.LayoutModel.Tabs.Count > 0) ActiveTerminal.SetFocus();
    }

    /// <summary>
    /// 连接侧栏两个分区的单击、右键、新建与 Esc 回调。在构造窗口时调用一次。
    /// </summary>
    private void ConfigureSidebar()
    {
        spaceSection.RowActivated = row => Guard(() =>
        {
            if (projects.Projects.FirstOrDefault(p => p.Id == row.Key) is not { } project) return;
            // 单击当前空间只把焦点交给终端，不重新加载布局，也不关闭编辑器中打开的文件。
            if (!FileSystemPath.AreSame(project.Path, projectRoot)) SwitchProject(project);
            FocusActiveTerminal();
        });
        spaceSection.CreateRequested = () => Guard(AddProject);
        spaceSection.ContextRequested = row => Guard(() => SpaceMenu(row.Key));
        spaceSection.FocusReturnRequested = FocusActiveTerminal;
        agentSection.RowActivated = row => Guard(() =>
        {
            if (sessions.FirstOrDefault(s => s.Id == row.Key) is { } session) FocusSession(session);
        });
        agentSection.CreateRequested = () => Guard(NewAgent);
        agentSection.ContextRequested = row => Guard(() => AgentMenu(row.Key));
        agentSection.FocusReturnRequested = FocusActiveTerminal;
    }

    /// <summary>
    /// 按当前项目、会话与活动面板重建侧栏 <c>spaces</c> 与 <c>agents</c> 两个分区的行。
    /// </summary>
    /// <remarks>
    /// <c>spaces</c>：行首符号为该项目中所有会话状态的汇总（等待决定优先），右侧为 Git 分支，当前项目以粗体高亮；分区没有焦点时选中当前项目。
    /// <c>agents</c>：按 <see cref="sessions"/> 的顺序列出，右侧为“状态 · 程序”，活动面板正在显示的会话以粗体高亮；按会话标识保持原选择。
    /// 行数变化使分区高度（见 <see cref="SectionHeight"/>）改变时请求重新布局。
    /// </remarks>
    private void RefreshSidebar()
    {
        var heights = (SectionHeight(spaceSection, MaxSpaceRows), SectionHeight(agentSection, MaxAgentRows));
        var items = projects.Projects;
        var current = -1;
        var spaceRows = new List<SidebarRow>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var project = items[index];
            var isCurrent = FileSystemPath.AreSame(project.Path, projectRoot);
            if (isCurrent) current = index;
            var badge = AgentStateStyle.RollUp(sessions.Where(s => FileSystemPath.IsWithin(project.Path, s.WorkingDirectory)));
            spaceRows.Add(new(project.Id, badge.Glyph, badge.Color, project.Name, BranchOf(project.Path) ?? "", isCurrent));
        }
        spaceSection.SetRows(spaceRows, spaceSection.HasFocus ? null : Math.Max(0, current));
        var selectedId = agentSection.SelectedIndex is var selected and >= 0 ? agentSection.Rows[selected].Key : null;
        // 构造窗口时侧栏先于终端布局建立，此时还没有标签页，也就没有正在显示的会话。
        var shown = terminalLayout.LayoutModel.Tabs.Count == 0 ? null : ActiveTerminal.SessionId;
        var agentRows = sessions.Select(s =>
        {
            var badge = AgentStateStyle.For(s);
            return new SidebarRow(s.Id, badge.Glyph, badge.Color, DisplayName(s), AgentStateStyle.Describe(s), s.Id == shown);
        }).ToArray();
        agentSection.SetRows(agentRows, selectedId is null ? null : Math.Max(0, Array.FindIndex(agentRows, r => r.Key == selectedId)));
        if (heights != (SectionHeight(spaceSection, MaxSpaceRows), SectionHeight(agentSection, MaxAgentRows))) SetNeedsLayout();
    }

    /// <summary>
    /// 会话在侧栏与面板标题中的显示名称：通常就是会话名；另一个会话同名时附上会话标识的前 6 位以便区分。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <returns>显示名称，例如 <c>claude</c> 或 <c>claude #3f9a1c</c>。</returns>
    private string DisplayName(SessionInfo session) =>
        sessions.Any(s => s.Id != session.Id && s.Name == session.Name)
            ? $"{session.Name} #{session.Id[..Math.Min(6, session.Id.Length)]}"
            : session.Name;

    /// <summary>
    /// 返回项目所在仓库最近一次读到的 Git 分支，并在需要时于后台重新读取。
    /// </summary>
    /// <remarks>
    /// 读取涉及文件系统访问（向上查找 <c>.git</c>），网络路径或异常文件可能长时间阻塞，所以从不在 UI 线程上进行：
    /// 缓存超过 <see cref="BranchRefreshInterval"/> 且该路径没有进行中的读取时启动一次后台读取，结果回到 UI 线程写入缓存，
    /// 分支有变化时重建侧栏。第一次读取完成前返回 <see langword="null"/>。
    /// </remarks>
    /// <param name="path">项目路径。</param>
    /// <returns>最近读到的分支名；尚未读到或不在仓库中时为 <see langword="null"/>。</returns>
    private string? BranchOf(string path)
    {
        var known = branchCache.TryGetValue(path, out var cached);
        if ((!known || DateTime.UtcNow - cached.ReadAt >= BranchRefreshInterval) && !detached && branchReads.Add(path))
        {
            _ = Task.Run(() =>
            {
                string? branch;
                try { branch = GitBranch.Read(path); }
                catch (Exception error) when (error is not OutOfMemoryException) { branch = null; }
                if (!detached) application.Invoke(() =>
                {
                    branchReads.Remove(path);
                    var changed = !branchCache.TryGetValue(path, out var previous) || previous.Branch != branch;
                    branchCache[path] = (branch, DateTime.UtcNow);
                    if (changed && !detached) Guard(RefreshSidebar);
                });
            });
        }
        return known ? cached.Branch : null;
    }

    /// <summary>
    /// 空间行的菜单：打开该项目、在其中启动 Agent，或从侧栏移除登记（磁盘上的文件夹与会话都不受影响）。
    /// </summary>
    /// <param name="projectId">空间行对应的项目标识；项目已不存在时什么也不做。</param>
    private void SpaceMenu(string projectId)
    {
        if (projects.Projects.FirstOrDefault(p => p.Id == projectId) is not { } project) return;
        var choice = Dialogs.Choose(application, project.Name + "  ·  " + project.Path, ["Open", "Start an agent here", "Remove from sidebar"]);
        if (choice is 0 or 1 && !FileSystemPath.AreSame(projectRoot, project.Path)) SwitchProject(project);
        if (choice == 0) FocusActiveTerminal();
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
    /// 菜单标题是会话的显示名称与状态；除“显示”外的操作都先把该会话显示到活动面板，再调用作用于活动面板会话的现有命令。
    /// </remarks>
    /// <param name="sessionId">Agent 行对应的会话标识；会话已不在列表中时什么也不做。</param>
    private void AgentMenu(string sessionId)
    {
        if (sessions.FirstOrDefault(s => s.Id == sessionId) is not { } session) return;
        var choice = Dialogs.Choose(application, DisplayName(session) + "  ·  " + AgentStateStyle.Describe(session),
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
