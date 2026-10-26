using System.Collections.ObjectModel;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.UI.Controls;
using CrewTerm.App.UI.Input;
using CrewTerm.Automation.Agents;
using CrewTerm.Core.IO;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Projects;
using CrewTerm.Core.Settings;
using CrewTerm.Workspace.Checkpoints;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Files;
using CrewTerm.Workspace.Git;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// CrewTerm 的全屏工作台窗口：左侧项目与 Agent 会话列表，右侧终端标签页与分屏，下方文件列表与代码编辑器。
/// </summary>
/// <remarks>
/// <para>
/// 窗口本身不运行任何终端进程：所有会话都由后台 daemon 持有，窗口通过 <see cref="DaemonClient"/> 每 160 毫秒拉取
/// 会话列表与屏幕快照，并把键盘、鼠标输入经 <see cref="SessionInputDispatcher"/> 按序转发。
/// 因此分离（F10）或关闭窗口不会结束会话，重新打开后按保存的布局恢复。
/// </para>
/// <para>
/// 类按职责拆分为多个分部文件：本文件负责构造、全局快捷键、刷新循环、项目/会话/文件导航与释放；
/// <c>Actions</c> 为命令面板与常规操作，<c>Agents</c> 为 Agent 状态、通知与消息，<c>Extensions</c> 为会话恢复与本地插件，
/// <c>Layout</c> 为标签页与分屏，<c>Mouse</c> 为终端面板回调，<c>Tools</c> 为 Git、后台任务与检查点。
/// </para>
/// <para>
/// 线程约定：除标明在后台运行的代码外，所有成员都必须在 UI 线程调用；后台任务通过 <c>IApplication.Invoke</c>
/// 回到 UI 线程，并在 <see cref="detached"/> 置位后停止操作界面。操作失败统一由 <see cref="Guard"/> 以消息框报告。
/// </para>
/// </remarks>
public sealed partial class WorkspaceWindow : Window
{
    /// <summary>
    /// Terminal.Gui 应用实例：运行模态对话框、注册计时器、从后台线程切回 UI 线程，并提供剪贴板与全局键盘事件。
    /// </summary>
    private readonly IApplication application;

    /// <summary>
    /// 状态目录：决定连接哪个 daemon，并存放项目列表、工作台设置、Agent 配置、检查点与流水线模板。
    /// </summary>
    private readonly string stateDirectory;

    /// <summary>
    /// 正在运行的 daemon 版本较旧时的兼容性警告；为 <see langword="null"/> 表示协议兼容。
    /// </summary>
    /// <remarks>
    /// 非空时窗口标题、状态栏与 F1 帮助都会提示升级方法，需要新协议的请求在发送前就被拒绝（见 <see cref="Call"/>）。
    /// </remarks>
    private readonly string? serverCompatibilityWarning;

    /// <summary>
    /// 连接本状态目录对应 daemon 的命名管道客户端；每次调用建立一个新连接，可在任意线程使用。
    /// </summary>
    private readonly DaemonClient client;

    /// <summary>
    /// 侧栏中的项目列表，修改后需显式 <c>Save</c> 才会写盘。
    /// </summary>
    private readonly ProjectCatalog projects;

    /// <summary>
    /// 工作台设置（<see cref="settings"/>）的持久化存储。
    /// </summary>
    private readonly WorkspaceSettingsStore settingsStore;

    /// <summary>
    /// 启动时加载的工作台设置：上次的项目与会话、各项目的终端布局、命令模板、并发上限等。
    /// </summary>
    /// <remarks>
    /// 运行期间直接修改这个对象；只在退出、保存模板或修改设置时写盘。
    /// </remarks>
    private readonly WorkspaceSettings settings;

    /// <summary>
    /// Agent 配置目录（内置 CLI 与用户自定义命令），用于发现本机已安装的 Agent。
    /// </summary>
    private readonly AgentCatalog agents;

    /// <summary>
    /// 执行 Git 查询、暂存与 worktree 操作的服务。
    /// </summary>
    private readonly GitService git = new();

    /// <summary>
    /// 项目文件检查点的捕获、预览与恢复。
    /// </summary>
    private readonly CheckpointStore checkpoints;

    /// <summary>
    /// 下方代码编辑器控件与当前打开文件的绑定。
    /// </summary>
    private readonly EditorBinding editor = new();

    /// <summary>
    /// 左上方 “SPACES” 框中的项目列表，当前项目前显示 <c>●</c>。
    /// </summary>
    private readonly ListView projectList = new() { Width = Dim.Fill(), Height = Dim.Fill() };

    /// <summary>
    /// 左下方 “AGENTS” 框中的会话列表，各行与 <see cref="sessions"/> 一一对应。
    /// </summary>
    private readonly ListView sessionList = new() { Width = Dim.Fill(), Height = Dim.Fill() };

    /// <summary>
    /// 下方 “FILES” 框中的目录列表；不在项目根目录时第一行为 <c>../</c>，其余各行依次对应 <see cref="files"/>。
    /// </summary>
    private readonly ListView fileList = new() { Width = Dim.Fill(), Height = Dim.Fill() };

    /// <summary>
    /// 右上方的终端标签页与分屏视图，显示当前项目的终端布局。
    /// </summary>
    private readonly TerminalLayoutView terminalLayout = new();

    /// <summary>
    /// 当前标签页中视觉顺序的第一个终端面板。
    /// </summary>
    private TerminalPane primary => terminalLayout.Panes[0];

    /// <summary>
    /// 当前标签页中视觉顺序的第二个终端面板；只有一个面板时等于 <see cref="primary"/>。
    /// </summary>
    private TerminalPane secondary => terminalLayout.Panes.Count > 1 ? terminalLayout.Panes[1] : primary;

    /// <summary>
    /// 文件列表的外框，标题显示当前目录（项目相对路径）。
    /// </summary>
    private readonly FrameView filesFrame = new() { Title = "FILES" };

    /// <summary>
    /// 代码编辑器的外框，标题显示文件名，有未保存修改时前缀 <c>*</c>。
    /// </summary>
    private readonly FrameView editorFrame = new() { Title = "CODE  ·  F5 Save" };

    /// <summary>
    /// 倒数第二行的单行状态栏，显示操作结果、错误与 Agent 状态提醒。
    /// </summary>
    private readonly Label status = new() { Width = Dim.Fill(), Height = 1 };

    /// <summary>
    /// 窗口生命周期的取消源：释放窗口时取消，进行中的 daemon 调用、搜索、Git 与检查点操作都会收到取消信号。
    /// </summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>
    /// 终端输入的有序发送队列；释放窗口时先尝试把已接受的输入送完。
    /// </summary>
    private readonly SessionInputDispatcher inputDispatcher;

    /// <summary>
    /// 释放窗口时，已接受的终端输入是否在 5 秒内全部成功送达 daemon；窗口释放之前始终为 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 程序入口在窗口关闭后读取它：为 <see langword="false"/> 时提示用户有输入可能丢失，并以退出码 1 结束。
    /// </remarks>
    public bool InputDrained { get; private set; } = true;

    /// <summary>
    /// 160 毫秒刷新计时器的令牌，释放窗口时用它移除计时器；注册失败时为 <see langword="null"/>。
    /// </summary>
    private readonly object? refreshTimer;

    /// <summary>
    /// <c>polling</c>：是否有一次后台刷新尚未结束，用于防止刷新重叠；
    /// <c>detached</c>：窗口已开始释放（分离），之后后台回调不得再操作界面，刷新计时器也随之停止。
    /// </summary>
    /// <remarks>
    /// 后台刷新线程也会读取这两个标志，用 <see langword="volatile"/> 保证看到 UI 线程的最新写入。
    /// </remarks>
    private volatile bool polling, detached;

    /// <summary>
    /// <c>updatingLists</c>：正在以代码重建会话列表，期间忽略会话列表的选择操作；
    /// <c>filesVisible</c>：下方文件与编辑器区域是否显示（默认显示）；
    /// <c>exitApproved</c>：已确认可以退出（未保存修改与布局保存都已处理），之后窗口停止运行不再被拦截。
    /// </summary>
    private bool updatingLists, filesVisible = true, exitApproved;

    /// <summary>
    /// 当前项目以及切换项目时保护未保存编辑的规则。
    /// </summary>
    private readonly WorkspaceNavigation navigation = new();

    /// <summary>
    /// 当前项目的根目录；尚未选择项目时为 <see langword="null"/>。
    /// </summary>
    private string? projectRoot => navigation.ActiveProjectPath;

    /// <summary>
    /// 文件列表当前显示的目录，项目相对路径；空字符串表示项目根目录。
    /// </summary>
    private string relativeDirectory = "";

    /// <summary>
    /// 文件列表当前目录下的条目（目录在前，按名称排序）。
    /// </summary>
    private FileEntry[] files = [];

    /// <summary>
    /// 最近一次刷新得到的全部会话，按优先级降序、启动时间升序排列，与 <see cref="sessionList"/> 的各行对应。
    /// </summary>
    private SessionInfo[] sessions = [];

    /// <summary>
    /// 上次重建会话列表时的签名（各会话的标识、退出状态、退出码、活动修订号与未读数）；
    /// 签名未变时跳过重建，避免打断用户在列表中的选择与滚动。
    /// </summary>
    private string sessionSignature = "";

    /// <summary>
    /// 每个会话已经处理过的活动状态修订号，保证同一次状态变化只在状态栏提醒一次。
    /// </summary>
    private readonly Dictionary<string, long> activityRevisions = [];

    /// <summary>
    /// 按会话合并、串行发送终端尺寸变化的协调器。
    /// </summary>
    private readonly TerminalResizeCoordinator resizeCoordinator;

    /// <summary>
    /// 会话刷新的版本闸门：用户切换项目、会话、标签页或面板后，之前发起的刷新结果被丢弃。
    /// </summary>
    private readonly SessionRefreshGate refreshGate = new();

    /// <summary>
    /// 创建工作台窗口：加载状态目录中的项目与设置、搭建界面、恢复上次的项目与终端布局，并启动刷新计时器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须在 UI 线程、<see cref="IApplication"/> 初始化之后调用。构造时不会启动任何终端进程；
    /// 命令行指定了 <c>--project</c> 时先把该目录加入项目列表并设为当前项目，此时不恢复上次的会话选择。
    /// </para>
    /// <para>
    /// 窗口停止运行（分离、关闭）前会被拦截：先处理编辑器中的未保存修改并保存布局，用户取消则继续运行。
    /// 首次完成布局、终端面板有了实际尺寸后，焦点移到活动终端。
    /// </para>
    /// </remarks>
    /// <param name="application">已初始化的 Terminal.Gui 应用实例。</param>
    /// <param name="options">已解析的命令行，提供状态目录、可选的 <c>--project</c> 与 daemon 兼容性警告。</param>
    public WorkspaceWindow(IApplication application, CrewTermCommandLine options)
    {
        this.application = application;
        stateDirectory = options.StateDirectory;
        serverCompatibilityWarning = options.ServerCompatibilityWarning;
        client = new(stateDirectory);
        resizeCoordinator = new(async (id, columns, rows) =>
        {
            var response = await client.CallAsync(new("resize", id, Columns: columns, Rows: rows), lifetime.Token);
            if (!response.Ok) throw new IOException(response.Error ?? "Resize failed");
        });
        projects = new(stateDirectory);
        settingsStore = new(stateDirectory);
        settings = settingsStore.Load();
        agents = new(stateDirectory);
        checkpoints = new(stateDirectory);
        Title = serverCompatibilityWarning is null ? "CrewTerm  ·  MULTI-AGENT WORKSPACE"
            : "CrewTerm  ·  OLD SERVER · F1 for upgrade instructions";
        Width = Dim.Fill(); Height = Dim.Fill();
        SetScheme(new Scheme
        {
            Normal = new(new Color(0xc6d1df), new Color(0x10151d)),
            Focus = new(new Color(0xffffff), new Color(0x25425c)),
            HotNormal = new(new Color(0x68dbbf), new Color(0x10151d))
        });
        var spaces = new FrameView { Title = "SPACES  ·  F2", Width = 28, Height = Dim.Percent(32) };
        var crew = new FrameView { Title = "AGENTS  ·  F3", Width = 28, Y = Pos.Bottom(spaces), Height = Dim.Fill(2) };
        spaces.Add(projectList); crew.Add(sessionList);
        terminalLayout.X = 28; terminalLayout.Y = 0;
        terminalLayout.PaneCreated = ConfigureTerminalPane;
        terminalLayout.SelectionChanged = () =>
        {
            refreshGate.SelectionChanged();
            settings.LastSessionId = terminalLayout.ActivePane.SessionId;
        };
        terminalLayout.NewTabRequested = () => Guard(NewTab);
        terminalLayout.MenuRequested = () => Guard(Palette);
        filesFrame.Add(fileList); editorFrame.Add(editor.View);
        status.Y = Pos.AnchorEnd(2);
        var shortcuts = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
            Text = "F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git  F7 Tasks  F8 Split  F9 Commands  F10 Detach" };
        Add(spaces, crew, terminalLayout, filesFrame, editorFrame, status, shortcuts);
        ArrangePanels();
        projectList.Accepting += (_, e) => { e.Handled = true; Guard(SelectProject); };
        sessionList.Accepting += (_, e) => { e.Handled = true; Guard(SelectSession); };
        fileList.Accepting += (_, e) => { e.Handled = true; Guard(OpenFileEntry); };
        application.Keyboard.KeyDown += GlobalKey;
        IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || exitApproved) return;
            args.Cancel = true;
            Guard(() =>
            {
                if (!CanLeaveEditor()) return;
                if (!SaveLayoutForExit()) return;
                exitApproved = true;
                args.Cancel = false;
            });
        };
        editor.View.ModifiedChanged += (_, _) => UpdateEditorTitle();
        // 编辑器控件对 Ctrl+F 只发出事件，不自带查找框；与命令面板的 Editor: Find text 使用同一个查找框。
        editor.View.FindRequested += (_, _) => Guard(() => Dialogs.Find(application, editor.View, "Find in file"));
        if (options.Get("project") is string selected)
        {
            var project = projects.Add(selected); projects.Save(); settings.LastProjectId = project.Id;
        }
        var initial = projects.Projects.FirstOrDefault(p => p.Id == settings.LastProjectId) ?? projects.Projects.FirstOrDefault();
        if (initial is not null) { navigation.TrySelectSession(null, initial.Path); settings.LastProjectId = initial.Id; }
        RefreshProjects(); RefreshFiles();
        LoadProjectLayout(options.Get("project") is null ? settings.LastSessionId : null);
        var initialFocusPending = true;
        SubViewsLaidOut += (_, _) =>
        {
            var pane = ActiveTerminal;
            if (!initialFocusPending || pane.Viewport.Width < 1 || pane.Viewport.Height < 1) return;
            initialFocusPending = false;
            pane.SetFocus();
        };
        inputDispatcher = new(async (id, text, cancellation) =>
        {
            var response = await client.CallAsync(new("input", id, Text: text), cancellation);
            if (!response.Ok) throw new IOException(response.Error ?? "Input failed");
        }, error => { if (!detached) application.Invoke(() => status.Text = "Input: " + error.Message); });
        refreshTimer = application.AddTimeout(TimeSpan.FromMilliseconds(160), () => { BeginRefresh(); return !detached; });
        status.Text = serverCompatibilityWarning ?? projectRoot ?? "F2 Add a project, then F3 start an agent. F9 lists all commands.";
        terminalLayout.ActivePane.SetFocus();
    }

    /// <summary>
    /// 按 <see cref="filesVisible"/> 重新设置终端区、文件框与编辑器框的位置和大小。
    /// </summary>
    /// <remarks>
    /// 左侧 28 列固定为项目与会话列表。显示文件区时终端区占 53% 高度，其下左侧是 30 列宽的文件列表、右侧是编辑器；
    /// 隐藏时终端区一直延伸到底部两行（状态栏与快捷键提示）之上。
    /// </remarks>
    private void ArrangePanels()
    {
        terminalLayout.Width = Dim.Fill();
        terminalLayout.Height = filesVisible ? Dim.Percent(53) : Dim.Fill(2);
        filesFrame.X = 28; filesFrame.Y = Pos.Bottom(terminalLayout);
        filesFrame.Width = 30; filesFrame.Height = Dim.Fill(2); filesFrame.Visible = filesVisible;
        editorFrame.X = Pos.Right(filesFrame); editorFrame.Y = filesFrame.Y;
        editorFrame.Width = Dim.Fill(); editorFrame.Height = Dim.Fill(2); editorFrame.Visible = filesVisible;
    }

    /// <summary>
    /// 应用级键盘处理，先于获得焦点的视图收到按键：分派工作台快捷键，其余按键在终端有焦点时编码后发给子程序。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在本窗口位于最上层（没有模态对话框）且尚未分离时生效。快捷键：F1 帮助、F2 添加项目、F3 新建 Agent、
    /// F4 显示/隐藏文件区、F5 保存、F6 Git、F7 后台任务、F8 切换分屏、F9 与 Ctrl+Shift+P 命令面板、F10 分离；
    /// Ctrl+Shift+V 粘贴、Ctrl+Shift+C 复制选区；Alt+1/Alt+2 聚焦第一/第二个终端，Alt+3 编辑器，Alt+4 文件列表，Alt+5 会话列表。
    /// </para>
    /// <para>
    /// 快捷键操作在 <see cref="Guard"/> 中执行。不是快捷键时，若某个终端面板拥有焦点且按键可以编码，
    /// 则标记为已处理并加入输入队列，使 Ctrl+C、Esc、Tab 等键交给子程序而不是被 Terminal.Gui 解释。
    /// </para>
    /// </remarks>
    /// <param name="sender">事件源（未使用）。</param>
    /// <param name="key">按下的键；被处理时标记为已处理。</param>
    private void GlobalKey(object? sender, Key key)
    {
        if (application.TopRunnable != this || detached) return;
        Action? action = key.KeyCode switch
        {
            KeyCode.F1 => ShowHelp,
            KeyCode.F2 => AddProject,
            KeyCode.F3 => NewAgent,
            KeyCode.F4 => ToggleFiles,
            KeyCode.F5 => SaveFile,
            KeyCode.F6 => GitMenu,
            KeyCode.F7 => TaskMenu,
            KeyCode.F8 => ToggleSplit,
            KeyCode.F9 => Palette,
            KeyCode.F10 => Detach,
            _ => null
        };
        if (key == Key.P.WithCtrl.WithShift) action = Palette;
        if (key == Key.V.WithCtrl.WithShift) action = Paste;
        if (key == Key.C.WithCtrl.WithShift) action = CopySelection;
        if (key == Key.D1.WithAlt) action = () => primary.SetFocus();
        if (key == Key.D2.WithAlt) action = () => { if (terminalLayout.Panes.Count > 1) secondary.SetFocus(); };
        if (key == Key.D3.WithAlt) action = () => { ShowFiles(); editor.View.SetFocus(); };
        if (key == Key.D4.WithAlt) action = () => { ShowFiles(); fileList.SetFocus(); };
        if (key == Key.D5.WithAlt) action = () => sessionList.SetFocus();
        if (action is not null) { key.Handled = true; Guard(action); return; }
        // 终端拥有焦点时，Ctrl+C、Ctrl+Q、Esc 与 Tab 都归终端所有（发给子程序），而不是交给 Terminal.Gui 处理。
        var terminal = terminalLayout.Panes.FirstOrDefault(p => p.HasFocus);
        if (terminal is not null && TerminalInput.Encode(key, terminal.Snapshot?.ApplicationCursorKeys ?? false) is string text)
        { key.Handled = true; QueueInput(terminal, text); }
    }

    /// <summary>
    /// 执行一个界面操作，把除 <see cref="OutOfMemoryException"/> 之外的异常以标题为 “CrewTerm” 的消息框显示。
    /// </summary>
    /// <remarks>
    /// 所有由按键、列表、命令面板与后台完成回调触发的操作都经过这里，避免一次失败的操作中断 UI 消息循环。
    /// 必须在 UI 线程调用；消息框是模态的，关闭后才返回。
    /// </remarks>
    /// <param name="action">要执行的操作。</param>
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { MessageBox.Query(application, "CrewTerm", error.Message, ["OK"]); }
    }

    /// <summary>
    /// 发起一次后台刷新：拉取会话列表与各面板的屏幕快照，然后回到 UI 线程更新界面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由 160 毫秒的计时器调用，启动会话或分屏后也会立即调用；上一次刷新尚未结束或窗口已分离时直接返回。
    /// 在 UI 线程记下刷新版本、当前项目与各面板绑定的会话，之后的 daemon 调用都在后台线程进行。
    /// </para>
    /// <para>
    /// 未绑定会话的面板会自动分配一个尚未显示、工作目录属于当前项目的会话（没有当前项目时不限目录）。
    /// 只为仍然存在的会话请求屏幕快照（不含纯文本）。
    /// </para>
    /// <para>
    /// 结果通过 <see cref="refreshGate"/> 应用：期间用户切换过选择则整批丢弃。应用时重新排序会话、提醒新的活动状态、
    /// 重建会话列表并尽量保持原选择，再更新每个面板的会话、快照、标题与终端尺寸，最后把绑定写回布局模型。
    /// 失败时在状态栏显示 “Server: ” 加错误信息。
    /// </para>
    /// </remarks>
    private void BeginRefresh()
    {
        if (polling || detached) return;
        polling = true;
        var generation = refreshGate.Capture();
        var selectedRoot = projectRoot;
        var selectedPanes = terminalLayout.Panes.Select(p => (Pane: p, Id: p.SessionId)).ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await client.CallAsync(new("list"), lifetime.Token);
                var fresh = list.Sessions ?? [];
                resizeCoordinator.Retain(fresh.Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
                var used = selectedPanes.Where(p => p.Id is not null).Select(p => p.Id!).ToHashSet();
                var updates = new List<(TerminalPane Pane, string? Id, CrewTerm.Terminal.Emulation.TerminalSnapshot? Screen)>();
                foreach (var selected in selectedPanes)
                {
                    var id = selected.Id;
                    if (id is null)
                    {
                        id = fresh.FirstOrDefault(s => !used.Contains(s.Id) && (selectedRoot is null ||
                            FileSystemPath.AreSame(s.WorkingDirectory, selectedRoot)))?.Id;
                        if (id is not null) used.Add(id);
                    }
                    var screen = id is null || !fresh.Any(s => s.Id == id) ? null
                        : (await client.CallAsync(new("screen", id, IncludeText: false), lifetime.Token)).Screen;
                    updates.Add((selected.Pane, id, screen));
                }
                if (!detached) application.Invoke(() => refreshGate.TryApply(generation, () =>
                {
                    if (detached) return;
                    var selectedId = sessionList.SelectedItem is int selected && selected >= 0 && selected < sessions.Length
                        ? sessions[selected].Id : null;
                    sessions = fresh.OrderByDescending(s => s.Priority).ThenBy(s => s.StartedAt).ToArray();
                    UpdateActivityAlerts();
                    RefreshSessionList(selectedId);
                    foreach (var update in updates)
                    {
                        update.Pane.SessionId = update.Id; update.Pane.Update(update.Screen);
                        terminalLayout.SetTitle(update.Pane, SessionTitle(update.Pane));
                        ResizePane(update.Pane);
                    }
                    terminalLayout.SaveBindings();
                }));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!detached) application.Invoke(() => status.Text = "Server: " + error.Message); }
            finally { polling = false; }
        });
    }

    /// <summary>
    /// 计算终端面板框的标题。
    /// </summary>
    /// <param name="pane">要显示标题的面板。</param>
    /// <returns>
    /// 会话在最近的会话列表中时为 “名称 · 活动状态 · 会话标识前 6 位”；面板未绑定会话时为 “TERMINAL · F3 New”；
    /// 绑定的会话已不在列表中（例如 daemon 重启后只剩保存的记录）时为 “SAVED SESSION · F9 Resume”。
    /// </returns>
    private string SessionTitle(TerminalPane pane)
    {
        var session = sessions.FirstOrDefault(s => s.Id == pane.SessionId);
        return session is null ? (pane.SessionId is null ? "TERMINAL  ·  F3 New" : "SAVED SESSION  ·  F9 Resume")
            : $"{session.Name}  ·  {ActivityLabel(session)}  ·  {session.Id[..Math.Min(6, session.Id.Length)]}";
    }

    /// <summary>
    /// 把面板视口尺寸同步给绑定的会话：列数限制在 2–300、行数限制在 2–120。
    /// </summary>
    /// <remarks>
    /// 面板未绑定会话或视口小于 2×2 时什么也不做。请求交给 <see cref="resizeCoordinator"/> 合并发送，本方法不等待结果；
    /// I/O、取消与超时异常被忽略——失败后已确认尺寸被清除，下一次刷新会重新发送。
    /// </remarks>
    /// <param name="pane">要同步尺寸的面板。</param>
    private void ResizePane(TerminalPane pane)
    {
        if (pane.SessionId is not string id || pane.Viewport.Width < 2 || pane.Viewport.Height < 2) return;
        var size = (Columns: Math.Clamp(pane.Viewport.Width, 2, 300), Rows: Math.Clamp(pane.Viewport.Height, 2, 120));
        var resize = resizeCoordinator.RequestAsync(id, size.Columns, size.Rows);
        _ = Task.Run(async () =>
        {
            try { await resize; }
            // resize 只是尽力而为：任何失败（daemon 已退出、响应无效等）都不能成为未观察的异常，下次布局时会重新发送。
            catch (Exception error) when (error is not OutOfMemoryException) { }
        });
    }

    /// <summary>
    /// 把面板的输入加入有序发送队列，发送给它绑定的会话。
    /// </summary>
    /// <remarks>
    /// 面板未绑定会话时只在状态栏提示按 F3 启动终端。入队前先让面板回到最新画面；
    /// 队列已满（2048 条）时输入被丢弃，并在状态栏提示稍后重试。本方法不会阻塞 UI 线程。
    /// </remarks>
    /// <param name="pane">输入来源面板。</param>
    /// <param name="text">已编码的输入文本（按键序列、鼠标上报或粘贴内容）。</param>
    private void QueueInput(TerminalPane pane, string text)
    {
        if (pane.SessionId is null) { status.Text = "F3 starts a terminal."; return; }
        pane.ScrollToBottom();
        if (!inputDispatcher.TryWrite(pane.SessionId, text)) status.Text = "Input queue full; input was not sent. Wait and retry.";
    }

    /// <summary>
    /// 重建项目列表（当前项目前显示 <c>●</c>），并选中当前项目；没有当前项目时选中第一项。
    /// </summary>
    private void RefreshProjects()
    {
        projectList.SetSource(new ObservableCollection<string>(projects.Projects.Select(p => (FileSystemPath.AreSame(p.Path, projectRoot) ? "● " : "  ") + p.Name)));
        if (projects.Projects.Count > 0) projectList.SelectedItem = Math.Max(0, projects.Projects.ToList().FindIndex(p => FileSystemPath.AreSame(p.Path, projectRoot)));
    }

    /// <summary>
    /// 按 <see cref="sessions"/> 重建会话列表，每行为 “[活动状态] 名称”。
    /// </summary>
    /// <remarks>
    /// 会话签名与上次相同时什么也不做，以免每次刷新都打断用户的选择。重建期间置位 <see cref="updatingLists"/>。
    /// </remarks>
    /// <param name="selectedId">重建前选中的会话标识，重建后尽量保持选中它；为 <see langword="null"/> 或已不存在时选中第一项。</param>
    private void RefreshSessionList(string? selectedId)
    {
        var signature = string.Join('|', sessions.Select(s => $"{s.Id}:{s.HasExited}:{s.ExitCode}:{s.Activity?.Revision}:{s.UnreadCount}"));
        if (sessionSignature == signature) return;
        sessionSignature = signature;
        updatingLists = true;
        var selected = selectedId is null ? 0 : Array.FindIndex(sessions, s => s.Id == selectedId);
        sessionList.SetSource(new ObservableCollection<string>(sessions.Select(s => $"[{ActivityLabel(s)}] {s.Name}")));
        if (sessions.Length > 0) sessionList.SelectedItem = Math.Clamp(selected, 0, sessions.Length - 1);
        updatingLists = false;
    }

    /// <summary>
    /// 重新读取 <see cref="relativeDirectory"/> 并重建文件列表，然后选中第一项、在外框标题中显示当前目录。
    /// </summary>
    /// <remarks>
    /// 没有当前项目时列表为空。不在根目录时第一行为 <c>../</c>；目录名前显示 <c>▸</c>。
    /// 列表不包含 <c>.git</c> 与符号链接等重解析点。
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">项目根目录已不存在。</exception>
    /// <exception cref="FileNotFoundException">当前子目录已不存在。</exception>
    /// <exception cref="UnauthorizedAccessException">目录越出项目根目录、经过符号链接或无权访问。</exception>
    private void RefreshFiles()
    {
        string? failure = null;
        try { files = projectRoot is null ? [] : FileWorkspace.ListDirectory(projectRoot, relativeDirectory).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 项目目录可能已被删除、移走或无权访问（例如上次打开的项目）：工作台仍要能打开，文件区显示为空并说明原因。
            files = [];
            failure = error.Message;
        }
        var labels = new List<string>();
        if (relativeDirectory.Length > 0) labels.Add("../");
        labels.AddRange(files.Select(f => (f.IsDirectory ? "▸ " : "  ") + f.Name));
        fileList.SetSource(new ObservableCollection<string>(labels));
        if (labels.Count > 0) fileList.SelectedItem = 0;
        filesFrame.Title = "FILES  ·  " + (failure is not null ? "unavailable" : relativeDirectory.Length == 0 ? "/" : relativeDirectory);
        if (failure is not null) status.Text = "Project folder unavailable: " + failure;
    }

    /// <summary>
    /// 按当前文件与修改状态更新编辑器外框标题：有未保存修改时前缀 <c>*</c>，没有打开文件时提示在文件列表中按 Enter 打开。
    /// </summary>
    private void UpdateEditorTitle() => editorFrame.Title = (editor.IsDirty ? "* " : "") +
        (editor.Path is null ? "CODE  ·  Enter a file to edit" : Path.GetFileName(editor.Path)) + "  ·  F5 Save";

    /// <summary>
    /// 在替换或关闭当前文件之前处理未保存的修改。
    /// </summary>
    /// <remarks>
    /// 没有未保存修改时直接通过。否则弹出 Save / Discard / Cancel：选择 Save 时先保存再通过；
    /// 选择 Discard 时通过但不改动编辑器内容（由调用方关闭或替换文件）；选择 Cancel 或关闭对话框时不通过。
    /// </remarks>
    /// <returns>可以离开当前文件时为 <see langword="true"/>。</returns>
    /// <exception cref="FileConflictException">选择保存，但文件在打开后已被外部修改或删除。</exception>
    private bool CanLeaveEditor()
    {
        if (!editor.IsDirty) return true;
        var answer = MessageBox.Query(application, "Unsaved changes", "Save edits before leaving this file?", ["_Save", "_Discard", "_Cancel"]);
        if (answer == 0) { editor.Save(); return true; }
        return answer == 1;
    }

    /// <summary>
    /// 切换到指定项目（即使它就是当前项目，也会重新加载布局与文件列表）。
    /// </summary>
    /// <remarks>
    /// 先处理未保存修改，用户取消则什么也不做。随后保存当前项目的布局与会话绑定、使进行中的刷新失效、关闭编辑器、
    /// 文件列表回到根目录，再加载目标项目的终端布局并刷新项目列表、文件列表与编辑器标题。终端会话本身不受影响。
    /// </remarks>
    /// <param name="project">目标项目。</param>
    private void SwitchProject(ProjectEntry project)
    {
        if (!CanLeaveEditor()) return;
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        editor.Close(true); navigation.TrySelectSession(null, project.Path, discardConfirmed: true); relativeDirectory = "";
        settings.LastProjectId = project.Id;
        LoadProjectLayout(); RefreshProjects(); RefreshFiles(); UpdateEditorTitle();
        status.Text = project.Path;
    }

    /// <summary>
    /// 在项目列表中按 Enter 时，切换到选中的项目。
    /// </summary>
    private void SelectProject()
    {
        if (projectList.SelectedItem is int index && index < projects.Projects.Count) SwitchProject(projects.Projects[index]);
    }

    /// <summary>
    /// 在会话列表中按 Enter 时，把选中的会话显示到终端区并聚焦。
    /// </summary>
    /// <remarks>
    /// 以代码重建列表期间忽略。必要时先切换到会话工作目录所属的项目（用户可能因未保存修改而取消）；
    /// 会话已显示在某个面板中时聚焦该面板，否则替换活动面板中显示的会话。该会话会被记为下次启动时恢复的会话。
    /// </remarks>
    private void SelectSession()
    {
        if (updatingLists || sessionList.SelectedItem is not int index || index >= sessions.Length) return;
        var session = sessions[index];
        if (!BindSessionProject(session.WorkingDirectory)) return;
        refreshGate.SelectionChanged();
        var pane = terminalLayout.Panes.FirstOrDefault(p => p.SessionId == session.Id) ?? ActiveTerminal;
        pane.SessionId = session.Id; pane.Update(null); pane.SetFocus();
        settings.LastSessionId = session.Id;
        status.Text = session.WorkingDirectory;
    }

    /// <summary>
    /// 确保当前项目就是某个工作目录所在的项目，在显示或启动该目录下的会话之前调用。
    /// </summary>
    /// <remarks>
    /// 工作目录不在项目列表中时自动加入并保存。目标与当前项目不同时：若编辑器有未保存修改先询问用户；
    /// 然后保存旧项目的布局、关闭编辑器、文件列表回到根目录，并加载新项目的布局与列表。
    /// </remarks>
    /// <param name="root">会话的工作目录。</param>
    /// <returns>当前项目已对应该目录时为 <see langword="true"/>；用户因未保存修改取消时为 <see langword="false"/>，状态保持不变。</returns>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在，无法加入项目列表。</exception>
    private bool BindSessionProject(string root)
    {
        var target = FileSystemPath.Normalize(root);
        var different = !FileSystemPath.AreSame(projectRoot, target);
        // 规则统一由 WorkspaceNavigation 判定：只有切换到另一个项目且编辑器有未保存内容时才需要用户决定。
        navigation.EditorDirty = editor.IsDirty;
        if (navigation.RequiresDiscard(target) && !CanLeaveEditor()) return false;
        var project = projects.Projects.FirstOrDefault(p => FileSystemPath.AreSame(p.Path, target));
        if (project is null) { project = projects.Add(target); projects.Save(); }
        navigation.TrySelectSession(null, project.Path, discardConfirmed: true);
        settings.LastProjectId = project.Id;
        if (different)
        {
            SaveCurrentLayout();
            editor.Close(true); relativeDirectory = "";
            LoadProjectLayout();
            RefreshProjects(); RefreshFiles(); UpdateEditorTitle();
        }
        return true;
    }

    /// <summary>
    /// 在文件列表中按 Enter 时：<c>../</c> 返回上级目录，目录则进入，文件则在编辑器中打开。
    /// </summary>
    private void OpenFileEntry()
    {
        if (fileList.SelectedItem is not int index || projectRoot is null) return;
        if (relativeDirectory.Length > 0)
        {
            if (index == 0) { relativeDirectory = Path.GetDirectoryName(relativeDirectory) ?? ""; RefreshFiles(); return; }
            index--;
        }
        if (index >= files.Length) return;
        var file = files[index];
        if (file.IsDirectory) { relativeDirectory = file.RelativePath; RefreshFiles(); }
        else OpenFile(file.RelativePath);
    }

    /// <summary>
    /// 在编辑器中打开当前项目内的文件并跳到指定行，然后显示文件区并聚焦编辑器。
    /// </summary>
    /// <remarks>
    /// 没有当前项目，或用户在处理未保存修改时选择取消，则什么也不做。
    /// </remarks>
    /// <param name="relativePath">项目相对路径。</param>
    /// <param name="line">要跳到的行号，从 1 开始；超出范围时取最近的有效行。</param>
    private void OpenFile(string relativePath, int line = 1)
    {
        if (projectRoot is null || !CanLeaveEditor()) return;
        editor.Open(projectRoot, relativePath, true); editor.GoToLine(line);
        ShowFiles(); UpdateEditorTitle(); editor.View.SetFocus();
    }

    /// <summary>
    /// 保存编辑器中的文件（F5），并更新标题与状态栏。
    /// </summary>
    /// <exception cref="FileConflictException">文件在打开后已被外部修改或删除，内容未写入。</exception>
    private void SaveFile()
    {
        if (editor.Path is null) { status.Text = "No file is open. Select a file in FILES (F4) first."; return; }
        editor.Save(); UpdateEditorTitle(); status.Text = "Saved " + editor.Path;
    }

    /// <summary>
    /// 显示或隐藏下方文件与编辑器区域（F4），并把焦点移到第一个终端面板。
    /// </summary>
    private void ToggleFiles() { filesVisible = !filesVisible; ArrangePanels(); primary.SetFocus(); }

    /// <summary>
    /// 显示下方文件与编辑器区域，不改变焦点。
    /// </summary>
    private void ShowFiles() { filesVisible = true; ArrangePanels(); }

    /// <summary>
    /// 切换分屏（F8）：只有一个面板时向右分屏；否则把当前标签页收回为第一个面板。
    /// </summary>
    /// <remarks>
    /// 收回时其余面板只是从布局中移除，它们绑定的会话继续在 daemon 中运行；第一个面板成为活动面板并获得焦点。
    /// </remarks>
    private void ToggleSplit()
    {
        if (terminalLayout.Panes.Count == 1) { SplitPane(SplitDirection.Right); return; }
        SaveCurrentLayout(); refreshGate.SelectionChanged();
        var tab = terminalLayout.LayoutModel.ActiveTab;
        tab.Root = tab.Panes.First(); tab.ActivePaneId = tab.Root.Id;
        terminalLayout.Rebuild(); primary.SetFocus(); settings.SplitTerminals = false;
    }
    /// <summary>
    /// 显示快捷键与用法说明（F1），内容包括命令行帮助与工作台操作提示；有 daemon 兼容性警告时显示在最前面。
    /// </summary>
    private void ShowHelp() => Dialogs.Text(application, "SHORTCUTS · CrewTerm",
        (serverCompatibilityWarning is null ? "" : serverCompatibilityWarning + "\n\n") + CrewTermHelp.Text +
        "\n\nEnter selects an item. F9 opens the searchable command palette.\nUse Alt+1/2/3/4/5 to move focus between terminals, editor, files and agents.\nThe embedded CLI receives ordinary keys, Ctrl+C, Esc and Tab.\nUse F9 → Terminal: Send reserved key for function keys reserved by CrewTerm.\nFile saves detect external changes made by agents and refuse to overwrite them.");

    /// <summary>
    /// 请求分离工作台（F10）：窗口停止运行，所有会话继续在 daemon 中运行。
    /// </summary>
    /// <remarks>
    /// 真正停止前会被构造函数中注册的拦截处理：先处理未保存修改并保存布局，用户取消则窗口继续运行。
    /// </remarks>
    private void Detach()
    {
        application.RequestStop(this);
    }

    /// <summary>
    /// 退出前保存当前布局与活动会话，并把工作台设置写盘。
    /// </summary>
    /// <returns>
    /// 保存成功，或写盘失败（<see cref="IOException"/>）但用户确认不保存布局也要退出时为 <see langword="true"/>；
    /// 用户选择取消时为 <see langword="false"/>。
    /// </returns>
    private bool SaveLayoutForExit()
    {
        SaveCurrentLayout();
        settings.LastSessionId = ActiveTerminal.SessionId;
        try { settingsStore.Save(settings); return true; }
        catch (IOException error)
        {
            return Dialogs.Confirm(application, "Layout could not be saved",
                error.Message + "\n\nDetach without saving this layout? Your source-file saves are unaffected.");
        }
    }

    /// <summary>
    /// 释放窗口：停止刷新，把已接受的终端输入送完，再取消所有进行中的后台操作。
    /// </summary>
    /// <remarks>
    /// 只有第一次以 <paramref name="disposing"/> 为 <see langword="true"/> 调用时执行清理：置位 <see cref="detached"/>，
    /// 解除全局键盘处理并移除刷新计时器；然后在当前（UI）线程同步等待最多 5 秒，让输入队列中的输入全部送达，
    /// 结果写入 <see cref="InputDrained"/>；最后释放输入队列并取消、释放 <see cref="lifetime"/>。
    /// 终端会话不受影响，继续在 daemon 中运行。
    /// </remarks>
    /// <param name="disposing">为 <see langword="true"/> 表示由 <c>Dispose()</c> 调用，需要释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !detached)
        {
            detached = true;
            application.Keyboard.KeyDown -= GlobalKey;
            if (refreshTimer is not null) application.RemoveTimeout(refreshTimer);
            InputDrained = inputDispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            inputDispatcher.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
