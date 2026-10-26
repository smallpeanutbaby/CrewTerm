using CrewTerm.App.Protocol;
using CrewTerm.App.UI.Controls;
using CrewTerm.App.UI.Input;
using CrewTerm.Core.Editing;
using CrewTerm.Core.IO;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Security;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：命令面板与常规操作——项目与 Agent 配置管理、启动与关闭会话、剪贴板与滚动历史、
/// 文件打开与查找、向 Agent 发送代码上下文、设置、停止服务，以及同步 daemon 调用 <see cref="Call"/>
/// 和后台工作辅助方法 <see cref="RunWork{T}"/>。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 当前活动的终端面板（获得焦点或最近选中的面板），大多数终端命令都作用于它。
    /// </summary>
    private TerminalPane ActiveTerminal => terminalLayout.ActivePane;

    /// <summary>
    /// 当前项目的根目录，供必须有项目才能执行的命令使用。
    /// </summary>
    /// <exception cref="InvalidOperationException">尚未添加或选择项目，提示用户按 F2。</exception>
    private string Root => projectRoot ?? throw new InvalidOperationException("Add or select a project first (F2).");

    /// <summary>
    /// 添加项目（F2）：询问一个已有文件夹的路径，加入项目列表并切换过去。
    /// </summary>
    /// <remarks>
    /// 默认值为当前项目目录（没有项目时为进程当前目录）；输入会去掉首尾空白与引号。
    /// 该目录已在列表中时直接切换到已有项目。取消或输入为空时什么也不做。
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">输入的文件夹不存在。</exception>
    private void AddProject()
    {
        var path = Dialogs.Prompt(application, "Add project", "Existing folder path", projectRoot ?? Environment.CurrentDirectory);
        if (string.IsNullOrWhiteSpace(path)) return;
        var project = projects.Add(path.Trim().Trim('"'));
        projects.Save(); SwitchProject(project);
    }

    /// <summary>
    /// 新建 Agent 终端（F3）：在当前项目根目录中启动一个 Agent CLI、shell 或自定义命令，并显示到活动面板。
    /// </summary>
    /// <remarks>
    /// 列表包含所有 Agent 配置（已安装的显示可执行文件路径，未安装的标为 “not installed”），最后一项为自定义命令；
    /// 自定义命令依次询问命令行（默认 <c>cmd.exe</c>）与侧栏显示名（默认与命令相同）。
    /// 选中已安装的配置时使用发现到的可执行文件完整路径构造命令行。任何一步取消都不会启动会话。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目、所选 Agent 未安装，或 daemon 拒绝创建会话。</exception>
    private void NewAgent()
    {
        var root = Root;
        var available = agents.Discover();
        var labels = available.Select(a => a.Profile.Name + (a.IsAvailable ? "  ·  " + a.ExecutablePath : "  ·  not installed")).ToList();
        labels.Add("Custom command…");
        var choice = Dialogs.Choose(application, "New agent · " + Path.GetFileName(root), labels);
        if (choice < 0) return;
        string? command, name;
        if (choice == available.Count)
        {
            command = Dialogs.Prompt(application, "Custom terminal", "Command (agent CLI, shell or ssh)", "cmd.exe");
            if (string.IsNullOrWhiteSpace(command)) return;
            name = Dialogs.Prompt(application, "Terminal name", "Sidebar display name", command);
        }
        else
        {
            var item = available[choice];
            if (!item.IsAvailable) throw new InvalidOperationException("Install " + item.Profile.Name + " and ensure its executable is on PATH, or add a custom profile.");
            command = (item.Profile with { Executable = item.ExecutablePath! }).CommandLine;
            name = item.Profile.Name;
        }
        if (name is null) return;
        StartSession(command, root, name);
    }

    /// <summary>
    /// 请求 daemon 创建新会话，并把它显示到活动面板。
    /// </summary>
    /// <remarks>
    /// 先确保当前项目对应 <paramref name="root"/>（用户可能因未保存修改而取消，此时不创建会话）。
    /// 创建成功后活动面板改为显示新会话并获得焦点，该会话记为下次恢复的会话，并立即发起一次刷新。
    /// </remarks>
    /// <param name="command">要运行的完整命令行。</param>
    /// <param name="root">会话的工作目录。</param>
    /// <param name="name">侧栏与面板标题中显示的名称。</param>
    /// <exception cref="InvalidOperationException">daemon 拒绝创建会话。</exception>
    private void StartSession(string command, string root, string name)
    {
        if (!BindSessionProject(root)) return;
        var response = Call(new("create", Command: command, WorkingDirectory: root, Name: name));
        refreshGate.SelectionChanged();
        var pane = ActiveTerminal;
        pane.SessionId = response.SessionId; pane.Update(null); pane.SetFocus();
        settings.LastSessionId = response.SessionId;
        status.Text = "Started " + name + " in " + root;
        BeginRefresh();
    }

    /// <summary>
    /// 在 UI 线程同步调用 daemon，并把业务失败转换为异常。
    /// </summary>
    /// <remarks>
    /// 会阻塞 UI 线程直到响应返回（单次调用最长约 15 秒），只用于快速的请求；耗时操作应使用 <see cref="RunWork{T}"/>。
    /// 连接旧版 daemon 时，需要新协议的请求在发送前就被拒绝。窗口释放时调用会被取消。
    /// </remarks>
    /// <param name="request">要发送的请求。</param>
    /// <returns><see cref="DaemonResponse.Ok"/> 为 <see langword="true"/> 的响应。</returns>
    /// <exception cref="InvalidOperationException">请求需要新协议但 daemon 版本较旧，或 daemon 返回失败（消息为其错误信息）。</exception>
    /// <exception cref="TimeoutException">无法在限定时间内连接 daemon。</exception>
    /// <exception cref="InvalidDataException">请求过大或响应无法解析。</exception>
    private DaemonResponse Call(DaemonRequest request)
    {
        if (serverCompatibilityWarning is not null && CrewTermVersion.RequiresCurrentProtocol(request))
            throw new InvalidOperationException(serverCompatibilityWarning);
        // Terminal.Gui 会安装 UI 同步上下文。管道操作的后续延续会尝试回到该上下文，
        // 因此不能直接在这里阻塞等待，而是放到线程池上运行后再同步等待结果，避免死锁。
        var response = Task.Run(() => client.CallAsync(request, lifetime.Token)).GetAwaiter().GetResult();
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Server request failed");
        return response;
    }

    /// <summary>
    /// 打开命令面板（F9 或 Ctrl+Shift+P）：列出全部内置命令与已启用插件的命令，输入文字过滤，选中后立即执行。
    /// </summary>
    /// <remarks>
    /// 插件命令每次打开时都向 daemon 重新读取；连接旧版 daemon 时只显示内置命令。
    /// 所选命令在当前调用栈中同步执行，其异常由调用方的 <see cref="Guard"/> 报告。
    /// </remarks>
    private void Palette()
    {
        var commands = new PaletteCommand[]
        {
            new("project.add", "Project: Add folder", AddProject),
            new("project.remove", "Project: Remove from sidebar", RemoveProject),
            new("agent.new", "Agent: New embedded terminal", NewAgent),
            new("agent.profile", "Agent: Save custom CLI / SSH profile", AddProfile),
            new("agent.profiles.remove", "Agent: Remove custom profile", RemoveProfile),
            new("agent.restore", "Agent: Resume original conversation", RestoreSession),
            new("agent.restart", "Agent: Restart saved command as new conversation", () => RestoreSavedSession(false)),
            new("agent.bind", "Agent: Bind native conversation / resume command", BindNativeSession),
            new("agent.forget", "Agent: Forget dormant session record", ForgetSession),
            new("agent.notifications", "Agent: Notifications", NotificationsMenu),
            new("agent.seen", "Agent: Mark selected as read", MarkSelectedRead),
            new("agent.explain", "Agent: Explain selected status", ExplainAgent),
            new("agent.message", "Agent: Send message", () => SendAgentMessage(false)),
            new("agent.handoff", "Agent: Handoff prompt", () => SendAgentMessage(true)),
            new("agent.inbox", "Agent: Inbox", AgentInbox),
            new("agent.wait", "Agent: Wait for selected state", WaitForAgent),
            new("agent.close", "Agent: Terminate selected session", CloseSession),
            new("agent.secondary", "Agent: Choose second split terminal", ChooseSecondary),
            new("terminal.split", "Terminal: Toggle split", ToggleSplit),
            new("terminal.split.right", "Terminal: Split right", () => SplitPane(SplitDirection.Right)),
            new("terminal.split.left", "Terminal: Split left", () => SplitPane(SplitDirection.Left)),
            new("terminal.split.down", "Terminal: Split down", () => SplitPane(SplitDirection.Down)),
            new("terminal.split.up", "Terminal: Split up", () => SplitPane(SplitDirection.Up)),
            new("terminal.pane.close", "Terminal: Close pane (keep session running)", ClosePane),
            new("terminal.pane.next", "Terminal: Focus next pane", NextPane),
            new("terminal.pane.session", "Terminal: Choose session for active pane", ChooseTerminalSession),
            new("tab.new", "Tab: New", NewTab),
            new("tab.rename", "Tab: Rename", RenameTab),
            new("tab.close", "Tab: Close (keep sessions running)", CloseTab),
            new("tab.next", "Tab: Next", () => SwitchTab(1)),
            new("tab.previous", "Tab: Previous", () => SwitchTab(-1)),
            new("tab.choose", "Tab: Choose", ChooseTab),
            new("terminal.history", "Terminal: View / search scrollback", ShowScrollback),
            new("terminal.copy", "Terminal: Copy visible screen", CopyScreen),
            new("terminal.copy.selection", "Terminal: Copy selected text", CopySelection),
            new("terminal.paste", "Terminal: Paste clipboard", Paste),
            new("terminal.key", "Terminal: Send reserved key (F1–F12 / Tab / Esc)", SendReservedKey),
            new("file.open", "Files: Open path / file:line:column", OpenPath),
            new("file.search", "Files: Search project text", SearchFiles),
            new("file.refresh", "Files: Refresh directory", RefreshFiles),
            new("file.save", "Editor: Save file", SaveFile),
            new("file.reload", "Editor: Reload file from disk", ReloadFile),
            new("file.goto", "Editor: Go to line", GoToLine),
            new("file.find", "Editor: Find text", FindInFile),
            new("file.context", "Editor: Send selected code to an agent", SendContext),
            new("git", "Git: Changes / diff / stage / worktrees", GitMenu),
            new("tasks", "Tasks: Run / history / cancel / pipelines", TaskMenu),
            new("checkpoint", "Checkpoints: Capture / preview / restore", CheckpointMenu),
            new("plugins.install", "Plugins: Install local folder", InstallPlugin),
            new("plugins.toggle", "Plugins: Enable / disable", TogglePlugin),
            new("plugins.inspect", "Plugins: Inspect local manifest", InspectPlugin),
            new("plugins.remove", "Plugins: Remove registration", RemovePlugin),
            new("plugins.history", "Plugins: Execution history", PluginHistory),
            new("settings", "Settings: Concurrency and state directory", SettingsMenu),
            new("help", "Help: Shortcuts and usage", ShowHelp),
            new("detach", "Workspace: Detach (keep all sessions running)", Detach),
            new("server.stop", "Workspace: Stop all sessions and background tasks", StopServer)
        }.Concat(PluginCommands()).ToArray();
        var choice = Dialogs.Choose(application, "COMMANDS · type to filter", commands.Select(c => c.Label).ToArray());
        if (choice >= 0) commands[choice].Execute();
    }

    /// <summary>
    /// 从侧栏移除一个项目登记并保存；磁盘上的文件夹与其中的会话都不受影响。
    /// </summary>
    private void RemoveProject()
    {
        var items = projects.Projects.ToArray();
        var index = Dialogs.Choose(application, "Remove project from sidebar", items.Select(p => p.Name + "  ·  " + p.Path).ToArray());
        if (index < 0) return;
        projects.Remove(items[index].Id); projects.Save(); RefreshProjects();
        status.Text = "Removed sidebar entry; folder remains on disk.";
    }

    /// <summary>
    /// 保存一个自定义 Agent 配置（如其他 CLI 或 SSH 连接），之后可在 F3 列表中启动。
    /// </summary>
    /// <remarks>
    /// 依次询问显示名、可执行文件名或完整路径（默认 <c>ssh</c>，去掉引号）与可选参数；任何一步取消都不保存。
    /// 新配置使用随机生成的标识。
    /// </remarks>
    /// <exception cref="ArgumentException">配置字段无效。</exception>
    private void AddProfile()
    {
        var name = Dialogs.Prompt(application, "Agent profile", "Display name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var executable = Dialogs.Prompt(application, "Agent profile", "Executable name or full path", "ssh");
        if (string.IsNullOrWhiteSpace(executable)) return;
        var arguments = Dialogs.Prompt(application, "Agent profile", "Arguments (optional)");
        if (arguments is null) return;
        agents.Upsert(new(Guid.NewGuid().ToString("N"), name, executable.Trim('"'), arguments));
        status.Text = "Profile saved. Open F3 to launch.";
    }

    /// <summary>
    /// 选择并删除一个自定义 Agent 配置；内置配置不在列表中。
    /// </summary>
    private void RemoveProfile()
    {
        var custom = agents.Profiles.Where(p => !p.IsBuiltIn).ToArray();
        var index = Dialogs.Choose(application, "Remove custom profile", custom.Select(p => p.Name).ToArray());
        if (index >= 0) agents.Remove(custom[index].Id);
    }

    /// <summary>
    /// 恢复一个已保存会话的原始对话（命令面板 “Agent: Resume original conversation”）。
    /// </summary>
    private void RestoreSession()
    {
        RestoreSavedSession(true);
    }

    /// <summary>
    /// 经用户确认后终止活动面板绑定的会话及其子进程，并让该面板不再绑定会话。
    /// </summary>
    /// <remarks>
    /// 活动面板未绑定会话或用户取消时什么也不做。
    /// </remarks>
    /// <exception cref="InvalidOperationException">daemon 关闭会话失败。</exception>
    private void CloseSession()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        if (!Dialogs.Confirm(application, "Terminate terminal", "Stop this session and its child processes?")) return;
        Call(new("close", pane.SessionId)); refreshGate.SelectionChanged(); pane.SessionId = null; pane.Update(null);
    }

    /// <summary>
    /// 选择一个会话显示在第二个分屏面板中（不包括第一个面板正在显示的会话）。
    /// </summary>
    /// <remarks>
    /// 必要时先切换到该会话所属的项目（用户可能取消）。会话已显示在某个面板中时只聚焦那个面板；
    /// 当前只有一个面板时先向右分屏，再把会话放到第二个面板并聚焦。
    /// </remarks>
    private void ChooseSecondary()
    {
        var candidates = sessions.Where(s => s.Id != primary.SessionId).ToArray();
        var index = Dialogs.Choose(application, "Second terminal", candidates.Select(s => s.Name + " · " + s.Id).ToArray());
        if (index < 0) return;
        var session = candidates[index];
        if (!BindSessionProject(session.WorkingDirectory)) return;
        refreshGate.SelectionChanged();
        var existing = terminalLayout.Panes.FirstOrDefault(pane => pane.SessionId == session.Id);
        if (existing is not null) { existing.SetFocus(); return; }
        if (terminalLayout.Panes.Count == 1) SplitPane(SplitDirection.Right);
        secondary.SessionId = session.Id; settings.SplitTerminals = true;
        secondary.Update(null); secondary.SetFocus();
    }

    /// <summary>
    /// 把剪贴板文本粘贴到活动终端（Ctrl+Shift+V）。
    /// </summary>
    /// <remarks>
    /// 剪贴板不可用或为空时什么也不做。文本先去掉 <c>ESC</c> 与 C1 控制字符（见 <see cref="TerminalPaste"/>），
    /// 防止剪贴板内容提前结束 bracketed paste 或注入按键。子程序启用了 bracketed paste 时用 <c>ESC [200~</c> 与 <c>ESC [201~</c> 包裹文本；
    /// 未启用而文本含换行或回车时先请用户确认，因为它们可能直接执行命令。
    /// </remarks>
    /// <exception cref="InvalidOperationException">文本超过 60,000 个字符。</exception>
    private void Paste()
    {
        var pane = ActiveTerminal;
        if (application.Clipboard is null || !application.Clipboard.TryGetClipboardData(out var clipboard) || string.IsNullOrEmpty(clipboard)) return;
        if (clipboard.Length > 60000) throw new InvalidOperationException("Paste is limited to 60,000 characters. Select a smaller range.");
        var text = TerminalPaste.RemoveEscapes(clipboard);
        var bracketed = pane.Snapshot?.BracketedPaste == true;
        if (TerminalPaste.HasLineBreak(text) && !bracketed &&
            !Dialogs.Confirm(application, "Multiline paste", "This terminal has not enabled bracketed paste. Newlines may execute commands. Paste anyway?")) return;
        QueueInput(pane, bracketed ? TerminalPaste.Bracketed(text) : text);
    }

    /// <summary>
    /// 把活动会话当前可见屏幕的纯文本复制到剪贴板；活动面板未绑定会话时什么也不做。
    /// </summary>
    /// <exception cref="InvalidOperationException">剪贴板不可用，或 daemon 读取屏幕失败。</exception>
    private void CopyScreen()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        if (application.Clipboard is null) throw new InvalidOperationException("Clipboard unavailable in this terminal.");
        application.Clipboard.SetClipboardData(Call(new("screen", pane.SessionId)).Text ?? "");
        status.Text = "Terminal screen copied.";
    }

    /// <summary>
    /// 在只读查看器中显示活动会话的滚动历史与当前屏幕文本，可用 Ctrl+F 查找；活动面板未绑定会话时什么也不做。
    /// </summary>
    private void ShowScrollback()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        var response = Call(new("screen", pane.SessionId));
        Dialogs.Text(application, "SCROLLBACK · Ctrl+F to find", string.Join('\n', response.Screen?.History ?? []) + "\n" + response.Text);
    }

    /// <summary>
    /// 选择一个被工作台占用的按键（F1–F12、Tab、Esc）并把它的编码发送给活动终端。
    /// </summary>
    private void SendReservedKey()
    {
        var pane = ActiveTerminal;
        Key[] keys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12, Key.Tab, Key.Esc];
        var index = Dialogs.Choose(application, "Send key to terminal", keys.Select(k => k.ToString()).ToArray());
        if (index >= 0) QueueInput(pane, TerminalInput.Encode(keys[index])!);
    }

    /// <summary>
    /// 按输入的路径打开文件，路径可以带 <c>:行号:列号</c> 后缀。
    /// </summary>
    /// <remarks>
    /// 能解析出带行号的文件位置（<c>path:line[:column]</c> 或 <c>path(line[,column])</c>）时打开该文件并跳到对应行（列号不使用）；
    /// 否则把输入当作纯路径打开，绝对路径会先转换为相对项目根目录的路径。取消或留空时什么也不做。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目。</exception>
    /// <exception cref="UnauthorizedAccessException">文件位于项目之外或经过符号链接。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    private void OpenPath()
    {
        var path = Dialogs.Prompt(application, "Open file", "Relative or absolute file path, optionally :line:column");
        if (string.IsNullOrWhiteSpace(path)) return;
        var location = FileReferenceParser.Parse(path, Root);
        if (location is not null) OpenFile(Path.GetRelativePath(Root, location.Path), location.Line);
        else OpenFile(Path.IsPathRooted(path) ? Path.GetRelativePath(Root, path) : path);
    }

    /// <summary>
    /// 从磁盘重新加载当前文件（有未保存修改时先询问），光标回到第一行；没有打开文件时什么也不做。
    /// </summary>
    private void ReloadFile()
    {
        if (editor.Path is null) return;
        OpenFile(Path.GetRelativePath(Root, editor.Path));
    }

    /// <summary>
    /// 询问行号并在编辑器中跳转，然后显示文件区并聚焦编辑器；输入不是整数时什么也不做。
    /// </summary>
    private void GoToLine()
    {
        var line = Dialogs.Prompt(application, "Go to line", "Line number", "1");
        if (int.TryParse(line, out var value)) { editor.GoToLine(value); ShowFiles(); editor.View.SetFocus(); }
    }

    /// <summary>
    /// 询问要查找的文本，在编辑器中从光标处查找下一处（不区分大小写）并选中，然后显示文件区并聚焦编辑器。
    /// </summary>
    private void FindInFile()
    {
        var query = Dialogs.Prompt(application, "Find in file", "Text");
        if (string.IsNullOrEmpty(query)) return;
        editor.View.FindNext(query, false, false); ShowFiles(); editor.View.SetFocus();
    }

    /// <summary>
    /// 在当前项目中搜索文本（字面量、不区分大小写），列出匹配行，选中后打开对应文件并跳到该行。
    /// </summary>
    /// <remarks>
    /// 搜索在后台运行，期间界面保持可用；结果返回时如果用户已切换到其他项目，只显示结果而不打开文件。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目。</exception>
    private void SearchFiles()
    {
        var root = Root;
        var query = Dialogs.Prompt(application, "Search project", "Text (literal, case insensitive)");
        if (string.IsNullOrEmpty(query)) return;
        RunWork("Searching…", () => CrewTerm.Workspace.Files.FileWorkspace.SearchAsync(root, query, lifetime.Token), results =>
        {
            var index = Dialogs.Choose(application, $"SEARCH · {results.Count} matches", results.Select(r => $"{r.RelativePath}:{r.LineNumber}  {r.Text}").ToArray());
            if (index >= 0 && FileSystemPath.AreSame(projectRoot, root)) OpenFile(results[index].RelativePath, results[index].LineNumber);
        });
    }

    /// <summary>
    /// 把编辑器中选中的代码（没有选区时为光标所在行）连同指令插入某个 Agent 终端的输入区，不自动提交。
    /// </summary>
    /// <remarks>
    /// 依次询问指令（默认 “Review this code”）与目标会话（仅限未退出的会话）。文本以 bracketed paste 方式进入与按键相同的有序输入队列，
    /// 不会与该会话尚未送达的按键交错；用户需在目标终端中检查后自行按 Enter 提交。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有打开文件、目标 CLI 未启用 bracketed paste（避免代码被当作 shell 命令执行）、输入队列已满，或 daemon 调用失败。</exception>
    private void SendContext()
    {
        if (editor.Path is null || editor.View.Document is null) throw new InvalidOperationException("Open a file and select code first.");
        var doc = editor.View.Document;
        var start = editor.View.HasSelection ? Math.Min(editor.View.SelectionStart, editor.View.SelectionEnd) : editor.View.CaretOffset;
        var end = editor.View.HasSelection ? Math.Max(editor.View.SelectionStart, editor.View.SelectionEnd) - 1 : start;
        var first = doc.GetLocation(start).Line;
        var last = doc.GetLocation(Math.Max(start, end)).Line;
        var instruction = Dialogs.Prompt(application, "Send code context", $"Lines {first}–{last}: instruction", "Review this code");
        if (instruction is null) return;
        var targets = sessions.Where(s => !s.HasExited).ToArray();
        var index = Dialogs.Choose(application, "Send selected code to agent", targets.Select(s => s.Name + " · " + s.Id).ToArray());
        if (index < 0) return;
        var id = targets[index].Id;
        var screen = Call(new("screen", id)).Screen;
        if (screen?.BracketedPaste != true) throw new InvalidOperationException("Target CLI must enable bracketed paste before sending code. This avoids executing code as shell commands.");
        var context = editor.CreateContext(instruction, first, last);
        // 代码可能来自不受信任的仓库，TerminalPaste 会去掉其中可能提前结束 bracketed paste 的转义字符。
        if (!inputDispatcher.TryWrite(id, TerminalPaste.Bracketed(context)))
            throw new InvalidOperationException("Input queue full; the code was not sent. Wait and retry.");
        status.Text = "Code inserted into " + targets[index].Name + ". Review and press Enter in that terminal to submit.";
    }

    /// <summary>
    /// 修改 daemon 后台任务的最大并发数（1–32）并保存设置，然后显示状态目录与生效方式说明。
    /// </summary>
    /// <remarks>
    /// 新的并发数要在 daemon 重启后才生效。
    /// </remarks>
    /// <exception cref="ArgumentException">输入不是 1–32 之间的整数。</exception>
    private void SettingsMenu()
    {
        var value = Dialogs.Prompt(application, "Settings", "Maximum concurrent background tasks (1–32; applied after server restart)", settings.MaximumConcurrentTasks.ToString());
        if (value is null) return;
        if (!int.TryParse(value, out var count) || count is < 1 or > 32) throw new ArgumentException("Enter a number between 1 and 32.");
        settings.MaximumConcurrentTasks = count; settingsStore.Save(settings);
        Dialogs.Text(application, "Settings saved", "State directory:\n" + stateDirectory + "\n\nUse 'Workspace: Stop all' and reopen to apply concurrency. Existing terminals will stop only when explicitly requested.");
    }

    /// <summary>
    /// 停止工作台服务：终止所有嵌入终端、取消所有后台任务，然后关闭窗口。
    /// </summary>
    /// <remarks>
    /// 依次处理未保存修改、请求用户确认、保存布局，任何一步取消都不会停止。
    /// 发送 <c>stop</c> 后直接批准退出，不再经过退出拦截。
    /// </remarks>
    private void StopServer()
    {
        if (!CanLeaveEditor()) return;
        if (!Dialogs.Confirm(application, "Stop workspace server", "Terminate ALL embedded terminals and cancel ALL running tasks?")) return;
        if (!SaveLayoutForExit()) return;
        Call(new("stop")); exitApproved = true; application.RequestStop(this);
    }

    /// <summary>
    /// 在线程池上执行耗时工作，完成后回到 UI 线程处理结果，期间界面保持响应。
    /// </summary>
    /// <remarks>
    /// 开始时在状态栏显示 <paramref name="label"/>。成功时把状态栏改为 “Ready” 并调用 <paramref name="completed"/>；
    /// 失败时以 “标签 + 错误信息” 弹出消息框。两种情况都在 <see cref="Guard"/> 中执行；窗口已分离时什么也不做。
    /// 本方法立即返回，不等待工作完成。
    /// </remarks>
    /// <typeparam name="T">后台工作的结果类型。</typeparam>
    /// <param name="label">状态栏中显示的进度文字，也用作错误消息的前缀。</param>
    /// <param name="work">在线程池上运行的工作。</param>
    /// <param name="completed">在 UI 线程处理结果的回调；它抛出的异常同样以消息框报告。</param>
    private void RunWork<T>(string label, Func<Task<T>> work, Action<T> completed)
    {
        status.Text = label;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work();
                if (!detached) application.Invoke(() => { if (!detached) Guard(() => { status.Text = "Ready"; completed(result); }); });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!detached) application.Invoke(() => { if (!detached) Guard(() => throw new IOException(label + " " + error.Message, error)); }); }
        });
    }
}
