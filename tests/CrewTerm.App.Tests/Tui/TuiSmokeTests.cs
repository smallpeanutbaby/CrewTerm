using CrewTerm.App.Client;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 工作区界面的冒烟测试：在伪终端中启动真实的 <c>crewterm</c> 可执行文件，连接独立临时状态目录中的真实 daemon，
/// 覆盖多客户端布局保存冲突、命令面板与新建终端向导、跨项目会话选择的持久化、单击空间与侧栏 Esc 后的焦点、任务菜单、编辑器保存与未保存保护、分割终端的输入路由，以及帮助窗口与分离。
/// </summary>
[Collection(TuiCollection.Name)]
public sealed class TuiSmokeTests
{
    /// <summary>
    /// 验证：两个客户端连接同一状态目录时，第一个客户端按 F8 开启分割终端后分离并保存布局；第二个客户端分离时发现布局已被其他客户端修改，
    /// 提示 <c>Layout could not be saved</c>；直接按 Enter 只关闭确认框（默认按钮是 Cancel），工作区保持打开；
    /// 再次分离并按 Alt+O 确认后以退出码 0 分离，且不会覆盖第一个客户端保存的分割设置。
    /// </summary>
    [Fact]
    public async Task ConcurrentLayoutChangeCanDetachWithoutOverwritingOtherClient()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var first = TerminalSession.Start("layout-first", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => first.Screen.GetText().Contains("F10 Detach"), first, "first client");
            using var second = TerminalSession.Start("layout-second", $"\"{executable}\" --state-dir \"{state}\"", root, 150, 45);
            await WaitUntil(() => second.Screen.GetText().Contains("F10 Detach"), second, "second client");
            first.Write("\u001b[19~"); await Task.Delay(250); first.Write("\u001b[21~");
            await WaitUntil(() => first.HasExited, first, "first layout saved");
            Assert.True(new CrewTerm.Core.Settings.WorkspaceSettingsStore(state).Load().SplitTerminals);
            second.Write("\u001b[21~");
            await WaitUntil(() => second.Screen.GetText().Contains("Layout could not be saved"), second, "concurrent layout choice");
            // 确认框的默认按钮是 Cancel：直接按 Enter 只关闭对话框，工作区保持打开。
            second.Write("\r");
            await WaitUntil(() => !second.Screen.GetText().Contains("Layout could not be saved"), second, "Enter cancels the confirmation");
            Assert.False(second.HasExited);
            second.Write("\u001b[21~");
            await WaitUntil(() => second.Screen.GetText().Contains("Layout could not be saved"), second, "concurrent layout choice again");
            // Alt+O 明确选择 Confirm。
            second.Write("\u001bo");
            await WaitUntil(() => second.HasExited, second, "detach without overwriting layout");
            Assert.Equal(0, second.ExitCode);
            Assert.True(new CrewTerm.Core.Settings.WorkspaceSettingsStore(state).Load().SplitTerminals);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：在 F9 命令面板中输入 <c>checkpoints</c> 会立即滤掉无关命令，回车执行 <c>Checkpoints: Capture</c>；
    /// 在 F3 新建终端向导中选择 <c>Custom command</c> 并接受默认命令与名称后，启动真实的交互式 Shell（可执行 <c>echo</c>），daemon 中恰有一个存活会话。
    /// </summary>
    [Fact]
    public async Task CommandPaletteFiltersImmediatelyAndNewTerminalWizardStartsRealShell()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = TerminalSession.Start("wizard-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "wizard workspace");
            terminal.Write("\u001b[20~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Project: Add folder"), terminal, "command palette");
            terminal.Write("checkpoints");
            await WaitUntil(() => !terminal.Screen.GetText().Contains("Project: Add folder") && terminal.Screen.GetText().Contains("Checkpoints: Capture"), terminal, "palette filter");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Capture current project files"), terminal, "chosen palette action");
            terminal.Write("\u001b"); await Task.Delay(250); terminal.Write("\u001bOR");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Custom command"), terminal, "agent chooser");
            terminal.Write("custom command"); await Task.Delay(250); terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Custom terminal"), terminal, "custom command prompt");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Terminal name"), terminal, "terminal naming prompt");
            terminal.Write("\r");
            var wizardClient = new DaemonClient(state);
            string? wizardSession = null;
            await WaitUntil(() => (wizardSession = wizardClient.CallAsync(new("list")).GetAwaiter().GetResult().Sessions?.SingleOrDefault()?.Id) is not null,
                terminal, "embedded shell from wizard");
            await wizardClient.CallAsync(new("input", wizardSession, Text: "echo WIZARD_READY\r"));
            await WaitUntil(() => terminal.Screen.GetText().Contains("WIZARD_READY"), terminal, "wizard shell shown");
            terminal.Write("echo WIZARD_STARTED\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("WIZARD_STARTED"), terminal, "interactive wizard shell");
            var live = (await new DaemonClient(state).CallAsync(new("list"))).Sessions!;
            Assert.Single(live);
            Assert.False(live[0].HasExited);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "wizard UI detach");
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：以项目 A 启动后，在侧栏（Alt+5）中选择属于项目 B 的会话，会把工作区切换到项目 B（状态栏显示项目 B 的路径），分离后记录的最后项目为 B、最后会话为该会话；
    /// 不带 <c>--project</c> 重新连接时恢复到同一项目和会话；而以 <c>--project</c> 显式指定项目 A 启动时，不会把另一个根目录下保存的会话绑定到终端（键入后提示 <c>F3 starts a terminal.</c>）。
    /// </summary>
    [Fact]
    public async Task SelectingSessionInAnotherProjectPersistsMatchingFileWorkspace()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(state, "project-a");
        var b = Path.Combine(state, "project-b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "ONLY_IN_B.cs"), "// Project B");
        var client = new DaemonClient(state);
        try
        {
            // 外层测试 PTY 的作业对象要保留到清理阶段：释放它会有意杀死所有子孙进程；
            // 而界面进程自行退出时必须保留 daemon。
            using var terminal = TerminalSession.Start("binding-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{a}\"", root, 150, 45);
                await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "ready workspace A");
                var session = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: b, Name: "Agent B"));
                await WaitUntil(() => terminal.Screen.GetText().Contains("Agent B"), terminal, "B session in sidebar");
                // Alt+5 把焦点移到会话列表后再按 Enter；负载高时焦点切换可能晚于固定延时，未生效就重试。
                await RetryUntil(terminal, () => { terminal.Write("\u001b5"); return Task.Delay(300).ContinueWith(_ => terminal.Write("\r")); },
                    () => terminal.Screen.GetText().Contains(b));
                await WaitUntil(() => terminal.Screen.GetText().Contains(b), terminal, "B workspace");
                terminal.Write("\u001b[21~");
                await WaitUntil(() => terminal.HasExited, terminal, "detach B");
                var settings = new CrewTerm.Core.Settings.WorkspaceSettingsStore(state).Load();
                var project = new CrewTerm.Core.Projects.ProjectCatalog(state).Projects.Single(p => p.Id == settings.LastProjectId);
                Assert.Equal(b, project.Path);
                Assert.Equal(session.SessionId, settings.LastSessionId);
            using var reconnected = TerminalSession.Start("reconnected", $"\"{executable}\" --state-dir \"{state}\"", root, 150, 45);
            await WaitUntil(() => reconnected.Screen.GetText().Contains(b) && reconnected.Screen.GetText().Contains("Agent B"), reconnected, "consistent reconnect");
            reconnected.Write("\u001b[21~");
            await WaitUntil(() => reconnected.HasExited, reconnected, "reconnected detach");
            using var explicitlySelected = TerminalSession.Start("explicit-project", $"\"{executable}\" --state-dir \"{state}\" --project \"{a}\"", root, 150, 45);
            await WaitUntil(() => explicitlySelected.Screen.GetText().Contains("Agent B"), explicitlySelected, "connected server session list");
            await RetryUntil(explicitlySelected, () => { explicitlySelected.Write("\u001b1"); return Task.Delay(300).ContinueWith(_ => explicitlySelected.Write("x")); },
                () => explicitlySelected.Screen.GetText().Contains("F3 starts a terminal."));
            await WaitUntil(() => explicitlySelected.Screen.GetText().Contains("F3 starts a terminal."), explicitlySelected, "explicit project must not bind saved session from another root");
            explicitlySelected.Write("\u001b[21~");
            await WaitUntil(() => explicitlySelected.HasExited, explicitlySelected, "explicit project detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：在侧栏 <c>spaces</c> 中单击另一个项目会切换到该项目并把焦点交给终端——随后直接键入的命令送达该项目的会话；
    /// 用 Alt+5 聚焦侧栏 <c>agents</c> 后按 Esc 只把焦点还给终端，工作台不会因 Terminal.Gui 默认的 Esc 退出命令而分离，之后键入的命令同样送达会话。
    /// </summary>
    [Fact]
    public async Task ClickingASpaceHandsFocusToTheTerminalAndEscapeLeavesTheSidebar()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(state, "project-a");
        var b = Path.Combine(state, "project-b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        var catalog = new CrewTerm.Core.Projects.ProjectCatalog(state);
        catalog.Add(a); catalog.Add(b); catalog.Save();
        var client = new DaemonClient(state);
        try
        {
            using var terminal = TerminalSession.Start("space-focus-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{a}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach") && terminal.Screen.GetText().Contains("project-b"), terminal, "ready workspace with two spaces");
            var session = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: b, Name: "Agent B"));
            await WaitUntil(() => terminal.Screen.GetText().Contains("Agent B"), terminal, "B session in sidebar");
            // 窗口边框占第 0 行与第 0 列：spaces 标题在第 1 行，project-a 在第 2 行，project-b 在第 3 行（SGR 坐标从 1 开始）。
            terminal.Write("\u001b[<0;6;4M\u001b[<0;6;4m");
            await WaitUntil(() => terminal.Screen.GetText().Contains(b) && terminal.Screen.GetText().Contains("┤Agent B"), terminal, "B space shows its agent");
            terminal.Write("echo AFTER_SPACE_CLICK\r");
            await WaitForSessionText(client, session.SessionId!, "AFTER_SPACE_CLICK", terminal);
            await RetryUntil(terminal, () => { terminal.Write("\u001b5"); return Task.Delay(300); },
                () => terminal.Screen.Snapshot() is { CursorVisible: false });
            terminal.Write("\u001b");
            await Task.Delay(500);
            Assert.False(terminal.HasExited, "Esc in the sidebar must not detach the workspace.");
            terminal.Write("echo AFTER_SIDEBAR_ESC\r");
            await WaitForSessionText(client, session.SessionId!, "AFTER_SIDEBAR_ESC", terminal);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：在 F7 任务菜单中选择 <c>Run command</c>，用 Ctrl+A 全选后输入 <c>echo UI_TASK_MARKER</c> 并运行，界面显示 <c>TASK OUTPUT</c>、<c>EXIT 0</c> 和命令的真实输出，
    /// daemon 的任务列表中有对应的已完成且退出码为 0 的任务。
    /// </summary>
    [Fact]
    public async Task TaskMenuRunsCommandAndShowsRealOutput()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = TerminalSession.Start("task-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "task workspace");
            terminal.Write("\u001b[18~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Run command"), terminal, "task menu");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Run background command"), terminal, "command prompt");
            terminal.Write("\u0001echo UI_TASK_MARKER");
            await WaitUntil(() => terminal.Screen.GetText().Contains("UI_TASK_MARKER"), terminal, "entered command");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("TASK OUTPUT") && terminal.Screen.GetText().Contains("EXIT 0"), terminal, "task completion");
            Assert.Contains("UI_TASK_MARKER", terminal.Screen.GetText());
            var tasks = (await new DaemonClient(state).CallAsync(new("task-list"))).Tasks!;
            Assert.Contains(tasks, task => task.Completed && task.ExitCode == 0 && task.Command == "echo UI_TASK_MARKER");
            terminal.Write("\u001b"); await Task.Delay(250); terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "task UI detach");
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：从文件列表（Alt+4）打开 <c>hello.cs</c> 并编辑，按 F5 保存后磁盘内容包含该编辑；继续编辑但不保存时，Esc 触发的框架退出会弹出 <c>Unsaved changes</c> 保护而不退出，磁盘内容不变；
    /// 取消后按 F10 分离再次弹出保护，按 Alt+S 选择保存后才以退出码 0 退出，此时未保存的编辑已写入磁盘。
    /// </summary>
    [Fact]
    public async Task FileEditingSavesAndFrameworkQuitCannotDiscardDirtyBuffer()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(state, "project");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "hello.cs");
        File.WriteAllText(file, "// INITIAL_TEXT\n");
        try
        {
            using var terminal = TerminalSession.Start("editor-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{project}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "workspace ready");
            // 文件区默认隐藏：Alt+4 显示文件区并聚焦文件列表。
            terminal.Write("\u001b4");
            await WaitUntil(() => terminal.Screen.GetText().Contains("hello.cs"), terminal, "file browser");
            await Task.Delay(250); terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("INITIAL_TEXT"), terminal, "opened code");
            terminal.Write("SAVED_EDIT"); await Task.Delay(250); terminal.Write("\u001b[15~");
            await WaitUntil(() => File.ReadAllText(file).Contains("SAVED_EDIT"), terminal, "saved editor text");
            terminal.Write("UNSAVED_EDIT"); await Task.Delay(250);
            terminal.Write("\u001b4"); await Task.Delay(250); terminal.Write("\u001b");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Unsaved changes"), terminal, "guard for framework Escape quit");
            Assert.False(terminal.HasExited);
            Assert.DoesNotContain("UNSAVED_EDIT", File.ReadAllText(file));
            terminal.Write("\u001b"); await Task.Delay(300);
            Assert.False(terminal.HasExited);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Unsaved changes"), terminal, "detach guard after cancelling quit");
            // “保存”必须有显式的键盘快捷键（这里发送 Alt+S）；Enter 默认对应“取消”。
            terminal.Write("\u001bs");
            await WaitUntil(() => terminal.HasExited, terminal, "save and detach");
            Assert.Contains("UNSAVED_EDIT", File.ReadAllText(file));
            Assert.Equal(0, terminal.ExitCode);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：按 F8 分割后两个嵌入终端分别显示两个会话；用 Alt+1 / Alt+2 切换焦点后键入的命令只送达对应会话；
    /// 键入命令后紧接着按 F10 分离时，已排队的输入仍送达第二个会话，界面以退出码 0 退出，两个会话都仍在运行。
    /// 测试还会把当时的界面屏幕快照写入该测试的状态目录（<c>tui-split-snapshot.json</c>）供调试时查看，测试结束时随状态目录一并删除，不污染仓库目录。
    /// </summary>
    [Fact]
    public async Task SplitTerminalsRouteInputIndependentlyAndSurviveDetach()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var terminal = TerminalSession.Start("split-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 180, 50);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "ready workspace");
            var first = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Agent A"));
            var second = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Agent B"));
            await client.CallAsync(new("input", first.SessionId, Text: "echo CREW_A_READY\r"));
            await client.CallAsync(new("input", second.SessionId, Text: "echo CREW_B_READY\r"));
            terminal.Write("\u001b[19~"); // F8：分屏
            await WaitUntil(() => terminal.Screen.GetText().Contains("CREW_A_READY") && terminal.Screen.GetText().Contains("CREW_B_READY"), terminal, "two embedded terminals");
            // Alt 组合键作为独立的物理按键事件单独写入，而不是与后续文本拼成一个任意的 ESC 序列。
            // 焦点切换生效后，工作台把真实光标放进获得焦点的面板：按光标所在列判断焦点，而不是固定等待一段时间
            // （负载高的运行器上 250 毫秒不够，键入的文字会落到原来的面板）。
            var secondPaneColumn = PaneColumn(terminal, "Agent B");
            terminal.Write("\u001b1");
            await WaitUntil(() => terminal.Screen.Snapshot() is { CursorVisible: true } shot && shot.CursorColumn < secondPaneColumn, terminal, "focus on the first pane");
            terminal.Write("echo PRIMARY_ONLY\r");
            await WaitForSessionText(client, first.SessionId!, "PRIMARY_ONLY", terminal);
            terminal.Write("\u001b2");
            await WaitUntil(() => terminal.Screen.Snapshot() is { CursorVisible: true } shot && shot.CursorColumn > secondPaneColumn, terminal, "focus on the second pane");
            terminal.Write("echo SECONDARY_ONLY\r");
            await WaitForSessionText(client, second.SessionId!, "SECONDARY_ONLY", terminal);
            Assert.DoesNotContain("SECONDARY_ONLY", (await client.CallAsync(new("screen", first.SessionId))).Text);
            Assert.DoesNotContain("PRIMARY_ONLY", (await client.CallAsync(new("screen", second.SessionId))).Text);
            File.WriteAllText(Path.Combine(state, "tui-split-snapshot.json"), System.Text.Json.JsonSerializer.Serialize(terminal.Screen.Snapshot()));
            terminal.Write("echo QUEUED_BEFORE_DETACH\r\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "detach with live sessions");
            Assert.Equal(0, terminal.ExitCode);
            await WaitForSessionText(client, second.SessionId!, "QUEUED_BEFORE_DETACH");
            var sessions = (await client.CallAsync(new("list"))).Sessions!;
            Assert.Equal(2, sessions.Length);
            Assert.All(sessions, session => Assert.False(session.HasExited));
        }
        finally
        {
            try { await client.CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 返回界面中标题以 <paramref name="title"/> 开头的终端面板左边框所在的列（面板标题形如 <c>╭┤Agent B  ○ unknown · cmd</c>）。
    /// </summary>
    /// <param name="terminal">运行工作区界面的伪终端会话。</param>
    /// <param name="title">面板标题开头的会话名称。</param>
    /// <returns>该面板标题中 <c>┤</c> 所在的列（从 0 开始）；找不到该面板时使测试失败。</returns>
    private static int PaneColumn(ITerminalSession terminal, string title)
    {
        foreach (var line in terminal.Screen.GetText().Split('\n'))
            if (line.IndexOf("┤" + title, StringComparison.Ordinal) is >= 0 and var column) return column;
        Assert.Fail($"Missing pane {title}. Screen:\n{terminal.Screen.GetText()}");
        return -1;
    }

    /// <summary>
    /// 在 15 秒内每 75 毫秒向 daemon 读取一次会话屏幕，直到其中包含期望文本；超时则附上会话屏幕与外层界面屏幕内容使测试失败。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="id">会话标识。</param>
    /// <param name="expected">期望出现的文本。</param>
    /// <param name="ui">外层界面的伪终端会话，仅用于失败时输出诊断屏幕；界面已退出时可为 null。</param>
    /// <returns>表示等待过程的任务。</returns>
    private static async Task WaitForSessionText(DaemonClient client, string id, string expected, ITerminalSession? ui = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? text = null;
        while (DateTime.UtcNow < deadline)
        {
            text = (await client.CallAsync(new("screen", id))).Text;
            if (text?.Contains(expected) == true) return;
            await Task.Delay(75);
        }
        Assert.Fail($"Expected {expected}; actual terminal:\n{text}\nOuter UI:\n{ui?.Screen.GetText()}");
    }

    /// <summary>
    /// 验证：真实终端中工作区显示 <c>CrewTerm</c> 标题，F1 打开 <c>SHORTCUTS</c> 帮助窗口，按 Esc 关闭后按 F10 干净分离（退出码 0）。
    /// </summary>
    [Fact]
    public async Task RealTerminalShowsWorkspaceHelpAndDetachesCleanly()
    {
        var root = CrewTermBuild.Repository;
        var executable = CrewTermBuild.Executable;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = TerminalSession.Start("tui-test", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 140, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("CrewTerm"), terminal, "workspace header");
            terminal.Write("\u001bOP");
            await WaitUntil(() => terminal.Screen.GetText().Contains("SHORTCUTS"), terminal, "help window");
            terminal.Write("\u001b");
            await Task.Delay(300);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "clean detach");
            Assert.Equal(0, terminal.ExitCode);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 每 50 毫秒检查一次条件，最长等待 30 秒（界面进程提前退出也不会中止等待）；最后再检查一次，仍不成立则附上界面屏幕内容使测试失败。
    /// 上限按多个测试程序集并行、CPU 被占满的情况放宽，条件成立时立即返回。
    /// </summary>
    /// <param name="condition">要等待成立的条件。</param>
    /// <param name="terminal">运行工作区界面的伪终端会话。</param>
    /// <param name="behavior">失败信息中描述所等待行为的文字。</param>
    /// <returns>表示等待过程的任务。</returns>
    private static async Task WaitUntil(Func<bool> condition, ITerminalSession terminal, string behavior)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition() && !timeout.IsCancellationRequested) await Task.Delay(50);
        Assert.True(condition(), $"Missing {behavior}. Screen:\n{terminal.Screen.GetText()}");
    }

    /// <summary>
    /// 执行一个依赖焦点的按键操作，若 5 秒内结果条件仍未成立则重做，最多 3 次；不使测试失败（由随后的 <see cref="WaitUntil"/> 断言）。
    /// </summary>
    /// <remarks>
    /// 焦点切换（如 Alt+5）与随后的按键之间只能靠延时衔接；负载高时焦点可能晚于按键生效，按键落到别的控件上。
    /// 重做操作对这些幂等的步骤是安全的。
    /// </remarks>
    /// <param name="terminal">运行工作区界面的伪终端会话；进程退出时停止重试。</param>
    /// <param name="action">要执行的按键操作。</param>
    /// <param name="succeeded">操作已生效的判断条件。</param>
    /// <returns>表示重试过程的任务。</returns>
    private static async Task RetryUntil(ITerminalSession terminal, Func<Task> action, Func<bool> succeeded)
    {
        for (var attempt = 0; attempt < 3 && !succeeded() && !terminal.HasExited; attempt++)
        {
            await action();
            for (var waited = 0; waited < 100 && !succeeded(); waited++) await Task.Delay(50);
        }
    }
}
