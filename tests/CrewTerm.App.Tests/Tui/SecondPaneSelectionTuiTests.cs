using CrewTerm.App.Client;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Core.Projects;
using CrewTerm.Core.Settings;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 命令 <c>Agent: Choose second split terminal</c> 的端到端界面测试：在伪终端中启动真实的工作区并连接独立临时状态目录中的真实 daemon，
/// 验证选择已显示的会话、未显示的会话以及其他项目的会话时，面板绑定、焦点、输入路由和分离后保存的布局。
/// </summary>
public sealed class SecondPaneSelectionTuiTests
{
    /// <summary>
    /// 验证：分割出 3 个面板后，从主终端（Alt+1）选择已显示在第三个面板中的 Charlie，只把焦点移到该面板而不重复绑定：键入只送达 Charlie；
    /// 分离后保存的面板顺序仍为 Alpha、Bravo、Charlie，活动面板为第三个，所有会话都仍在运行。
    /// </summary>
    [Fact]
    public async Task ChoosingSessionAlreadyInThirdPaneFocusesItWithoutDuplicateBindings()
    {
        var root = CrewTermBuild.Repository;
        var state = NewState();
        var client = new DaemonClient(state);
        ITerminalSession? ui = null;
        try
        {
            ui = Launch(root, root, state);
            await Ready(ui);
            var a = await Create(client, root, "Second Alpha");
            var b = await Create(client, root, "Second Bravo");
            var c = await Create(client, root, "Second Charlie");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("Second Charlie"), ui, "three live sessions listed");
            await LayoutTuiTests.Command(ui, "Terminal: Split right");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("2 panes"), ui, "two panes");
            await LayoutTuiTests.Command(ui, "Terminal: Split down");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("3 panes"), ui, "three panes");
            ui.Write("\u001b1");
            await ChooseSecond(ui, "Second Charlie");
            ui.Write("echo SELECTED_THIRD_PANE\r");
            await LayoutTuiTests.WaitForText(client, c, "SELECTED_THIRD_PANE", ui);
            Assert.DoesNotContain("SELECTED_THIRD_PANE", (await client.CallAsync(new("screen", a))).Text);
            Assert.DoesNotContain("SELECTED_THIRD_PANE", (await client.CallAsync(new("screen", b))).Text);
            await Detach(ui);

            var settings = new WorkspaceSettingsStore(state).Load();
            var tab = settings.ProjectLayouts[settings.LastProjectId!].ActiveTab;
            var panes = tab.Panes.ToArray();
            Assert.Equal(new[] { a, b, c }, panes.Select(p => p.SessionId));
            Assert.Equal(panes[2].Id, tab.ActivePaneId);
            Assert.All((await client.CallAsync(new("list"))).Sessions!, session => Assert.False(session.HasExited));
        }
        finally { await Stop(client, ui, state); }
    }

    /// <summary>
    /// 验证：向右分割得到两个面板后选择尚未显示的 Charlie，只替换第二个面板：键入送达 Charlie；
    /// 分离后保存的面板为 Alpha、Charlie 且活动面板为第二个，daemon 中 3 个会话都仍然存在（被换下的会话没有被关闭）。
    /// </summary>
    [Fact]
    public async Task ChoosingAnUndisplayedSessionReplacesOnlySecondPane()
    {
        var root = CrewTermBuild.Repository;
        var state = NewState();
        var client = new DaemonClient(state);
        ITerminalSession? ui = null;
        try
        {
            ui = Launch(root, root, state);
            await Ready(ui);
            var a = await Create(client, root, "Second Alpha");
            await Create(client, root, "Second Bravo");
            var c = await Create(client, root, "Second Charlie");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("Second Charlie"), ui, "three live sessions listed");
            await LayoutTuiTests.Command(ui, "Terminal: Split right");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("2 panes"), ui, "two panes");
            await ChooseSecond(ui, "Second Charlie");
            ui.Write("echo REPLACED_SECOND_PANE\r");
            await LayoutTuiTests.WaitForText(client, c, "REPLACED_SECOND_PANE", ui);
            await Detach(ui);

            var settings = new WorkspaceSettingsStore(state).Load();
            var tab = settings.ProjectLayouts[settings.LastProjectId!].ActiveTab;
            var panes = tab.Panes.ToArray();
            Assert.Equal(new[] { a, c }, panes.Select(p => p.SessionId));
            Assert.Equal(panes[1].Id, tab.ActivePaneId);
            Assert.Equal(3, (await client.CallAsync(new("list"))).Sessions!.Length);
        }
        finally { await Stop(client, ui, state); }
    }

    /// <summary>
    /// 验证：以项目 A 启动工作区后，选择属于项目 B 的会话作为第二终端会把工作区切换到项目 B，键入送达该会话；
    /// 分离后最后选中的项目为 B，项目 A 的布局仍只有原来显示 Alpha 的单个面板，项目 B 活动标签中的活动面板显示 Bravo，Bravo 只出现一次且不含项目 A 的会话。
    /// </summary>
    [Fact]
    public async Task ChoosingAnotherProjectsSessionSwitchesWorkspaceAndPreservesOriginalLayout()
    {
        var root = CrewTermBuild.Repository;
        var state = NewState();
        var projectA = Path.Combine(state, "project-a");
        var projectB = Path.Combine(state, "project-b");
        Directory.CreateDirectory(projectA); Directory.CreateDirectory(projectB);
        await File.WriteAllTextAsync(Path.Combine(projectB, "ONLY_IN_B.txt"), "Project B");
        var client = new DaemonClient(state);
        ITerminalSession? ui = null;
        try
        {
            ui = Launch(root, projectA, state);
            await Ready(ui);
            var a = await Create(client, projectA, "Origin Alpha");
            var b = await Create(client, projectB, "Other Bravo");
            await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("Other Bravo") && ui.Screen.GetText().Contains("Origin Alpha  ·"), ui, "cross-project sessions listed");
            await ChooseSecond(ui, "Other Bravo");
            ui.Write("echo SECOND_IN_PROJECT_B\r");
            await LayoutTuiTests.WaitForText(client, b, "SECOND_IN_PROJECT_B", ui);
            await Detach(ui);

            var settings = new WorkspaceSettingsStore(state).Load();
            var projects = new ProjectCatalog(state).Projects;
            Assert.Equal(projectB, projects.Single(p => p.Id == settings.LastProjectId).Path);
            var originalProject = projects.Single(p => p.Path == projectA);
            Assert.Equal(a, Assert.Single(settings.ProjectLayouts[originalProject.Id].ActiveTab.Panes).SessionId);
            var active = settings.ProjectLayouts[settings.LastProjectId!].ActiveTab;
            Assert.Equal(b, active.Panes.Single(p => p.Id == active.ActivePaneId).SessionId);
            Assert.Single(active.Panes, p => p.SessionId == b);
            Assert.DoesNotContain(active.Panes, p => p.SessionId == a);
        }
        finally { await Stop(client, ui, state); }
    }

    /// <summary>
    /// 在系统临时目录下创建一个新的 <c>CrewTermSecondPaneUi\&lt;GUID&gt;</c> 状态目录。
    /// </summary>
    /// <returns>状态目录的完整路径。</returns>
    private static string NewState()
    {
        var path = Path.Combine(Path.GetTempPath(), "CrewTermSecondPaneUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 在 180×55 的伪终端中启动被测工作区（可执行文件见 <see cref="CrewTermBuild.Executable"/>），并以指定项目目录作为 <c>--project</c>。
    /// </summary>
    /// <param name="root">仓库根目录，作为进程工作目录。</param>
    /// <param name="project">通过 <c>--project</c> 打开的项目目录。</param>
    /// <param name="state">daemon 状态目录。</param>
    /// <returns>运行界面的伪终端会话。</returns>
    private static ITerminalSession Launch(string root, string project, string state)
        => TerminalSession.Start("second-pane-ui", $"\"{CrewTermBuild.Executable}\" --state-dir \"{state}\" --project \"{project}\"", root, 180, 55);

    /// <summary>
    /// 等待界面出现 <c>F10 Detach</c>，即工作区已就绪。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <returns>表示等待过程的任务。</returns>
    private static Task Ready(ITerminalSession ui) => LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");

    /// <summary>
    /// 在指定目录中创建一个运行交互式 Shell（<see cref="TestShell.Interactive"/>）的命名会话，并断言创建成功。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="directory">会话的工作目录（决定会话所属项目）。</param>
    /// <param name="name">会话显示名称。</param>
    /// <returns>新会话的标识。</returns>
    private static async Task<string> Create(DaemonClient client, string directory, string name)
    {
        var response = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: directory, Name: name));
        Assert.True(response.Ok, response.Error);
        return Assert.IsType<string>(response.SessionId);
    }

    /// <summary>
    /// 通过命令面板执行 <c>Agent: Choose second split terminal</c>，在 <c>Second terminal</c> 选择器中输入会话名过滤后按 Enter，并等待选择器关闭。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <param name="name">要选择的会话名称。</param>
    /// <returns>表示操作过程的任务。</returns>
    private static async Task ChooseSecond(ITerminalSession ui, string name)
    {
        await LayoutTuiTests.Command(ui, "Agent: Choose second split terminal");
        await LayoutTuiTests.Wait(() => ui.Screen.GetText().Contains("Second terminal"), ui, "second terminal chooser");
        ui.Write(name);
        await Task.Delay(200);
        ui.Write("\r");
        await LayoutTuiTests.Wait(() => !ui.Screen.GetText().Contains("Second terminal"), ui, "second terminal chosen");
    }

    /// <summary>
    /// 按 F10 分离，等待界面进程退出（布局随之保存），并断言其退出码为 0。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <returns>表示操作过程的任务。</returns>
    private static async Task Detach(ITerminalSession ui)
    {
        ui.Write("\u001b[21~");
        await LayoutTuiTests.Wait(() => ui.HasExited, ui, "detach and persist layout");
        Assert.Equal(0, ui.ExitCode);
    }

    /// <summary>
    /// 测试收尾：尽力向 daemon 发送 <c>stop</c>（忽略 I/O、超时与取消异常），最后释放伪终端会话。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="ui">运行工作区界面的伪终端会话；尚未启动时为 null。</param>
    /// <param name="state">本测试的临时状态目录，停止 daemon 后删除。</param>
    /// <returns>表示收尾过程的任务。</returns>
    private static async Task Stop(DaemonClient client, ITerminalSession? ui, string state)
    {
        try { await client.CallAsync(new("stop")); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
        finally { ui?.Dispose(); }
        await TestStateDirectory.DeleteAsync(state);
    }
}
