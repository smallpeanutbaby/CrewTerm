using CrewTerm.App.Client;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Settings;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 工作区终端布局的端到端测试：在伪终端中启动真实的 <c>crewterm</c> 界面并连接独立临时状态目录中的真实 daemon，
/// 通过按键与 SGR 鼠标序列驱动 TUI，覆盖右键菜单分割、初始焦点、滚动后回到实时输出、命名标签页与嵌套分割及其分离后的持久化恢复。
/// 同时提供其他 TUI 测试共用的启动、等待和命令面板辅助方法。
/// </summary>
[Collection(TuiCollection.Name)]
public sealed class LayoutTuiTests
{
    /// <summary>
    /// 验证：在嵌入终端上（第 50 列第 8 行）发送 SGR 右键按下与释放会弹出 <c>TERMINAL ACTIONS</c> 菜单，选择 <c>Split down</c> 后布局变为 2 个面板。
    /// </summary>
    [Fact]
    public async Task RightClickMenuCanSplitTheEmbeddedTerminal()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayoutUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("context-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var context = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Context shell"));
            await WaitForShellPrompt(client, context.SessionId!, ui);
            await client.CallAsync(new("input", context.SessionId, Text: "echo CONTEXT_READY\r"));
            // 先确认会话本身已输出，再等界面显示：失败时能分清是 Shell 还是界面的问题。
            await WaitForSessionScreen(client, context.SessionId!, ui, screen => screen.Contains("CONTEXT_READY"), "Session output missing");
            await Wait(() => ui.Screen.GetText().Contains("CONTEXT_READY"), ui, "terminal screen ready");
            ui.Write("\u001b[<2;50;8M\u001b[<2;50;8m");
            await Wait(() => ui.Screen.GetText().Contains("TERMINAL ACTIONS"), ui, "mouse context menu");
            ui.Write("Split down"); await Task.Delay(200); ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("2 panes"), ui, "mouse menu split");
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：工作区启动后创建的第一个会话自动绑定到终端面板并获得焦点，不按任何焦点快捷键直接键入的命令即可送达该会话。
    /// </summary>
    [Fact]
    public async Task InitialTerminalReceivesTypingWithoutAnExtraFocusShortcut()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayoutUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("initial-focus-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var created = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Initial shell"));
            await Wait(() => ui.Screen.GetText().Contains("┤Initial shell"), ui, "initial terminal bound");
            ui.Write("echo INITIAL_FOCUS\r");
            await WaitForText(client, created.SessionId!, "INITIAL_FOCUS", ui);
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：会话输出 80 行历史后，用 Alt+1 聚焦主终端并发送 SGR 滚轮向上使面板最上方的历史行号变小（已离开实时画面）；之后键入的命令送达会话，且面板自动回到实时输出并显示新命令的结果。
    /// </summary>
    [Fact]
    public async Task TypingAfterScrollReturnsToLiveTerminalOutput()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayoutUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("scroll-live-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var created = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Scroll shell"));
            await client.CallAsync(new("input", created.SessionId, Text: TestShell.CountLines("HISTORY_LINE_", 80) + "\r"));
            await Wait(() => ui.Screen.GetText().Contains("HISTORY_LINE_80"), ui, "history produced");
            var liveTop = TopHistoryLine(ui);
            ui.Write("\u001b1"); await Task.Delay(200);
            ui.Write("\u001b[<64;50;8M");
            // 面板可能比内容高（底部留有空行），因此以最上方可见的历史行号变小来判断已经向上滚动。
            await Wait(() => TopHistoryLine(ui) < liveTop, ui, "scrolled back");
            ui.Write("echo RETURNED_LIVE\r");
            await WaitForText(client, created.SessionId!, "RETURNED_LIVE", ui);
            await Wait(() => ui.Screen.GetText().Contains("RETURNED_LIVE"), ui, "typing returns to live output");
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 验证：先向右分割再向下嵌套分割得到 3 个面板后，键入只送达当前聚焦面板的会话（Charlie）；拖动分隔线调整比例，新建名为 Code_Review 的标签页（标签名中的下划线原样显示）后再点击标签栏回到原标签。
    /// 分离后保存的布局有 2 个标签，活动标签含 3 个面板、根为水平分割（比例 0.6–0.8）且第二子节点为垂直分割，所有会话仍在运行；
    /// 重新打开界面时恢复嵌套布局与屏幕内容，关闭一个面板只减少面板而不结束会话（daemon 中仍有 3 个会话）。
    /// </summary>
    [Fact]
    public async Task NamedTabsAndNestedPanesRouteInputAndRestoreAfterDetach()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLayoutUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("layout", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var a = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Pane Alpha"));
            var b = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Pane Bravo"));
            var c = await client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: root, Name: "Pane Charlie"));
            await Wait(() => ui.Screen.GetText().Contains("Pane Charlie"), ui, "sessions loaded");
            await Command(ui, "Terminal: Split right");
            await Wait(() => ui.Screen.GetText().Contains("2 panes"), ui, "horizontal split");
            await Command(ui, "Terminal: Split down");
            await Wait(() => ui.Screen.GetText().Contains("3 panes"), ui, "nested vertical split");
            ui.Write("echo CHARLIE_TARGET\r");
            await WaitForText(client, c.SessionId!, "CHARLIE_TARGET", ui);
            Assert.DoesNotContain("CHARLIE_TARGET", (await client.CallAsync(new("screen", a.SessionId))).Text);
            Assert.DoesNotContain("CHARLIE_TARGET", (await client.CallAsync(new("screen", b.SessionId))).Text);

            // SGR 鼠标事件先经过外层伪终端，再交给真实界面的鼠标捕获处理器：在第 6 行按住左键，从第 108 列（终端区从第 37 列开始，左右面板之间的分隔线所在列）拖到第 128 列后释放。
            ui.Write("\u001b[<0;108;6M"); await Task.Delay(100);
            ui.Write("\u001b[<32;128;6M"); await Task.Delay(100);
            ui.Write("\u001b[<0;128;6m");

            await Command(ui, "Tab: New");
            await Wait(() => ui.Screen.GetText().Contains("New tab name"), ui, "tab name prompt");
            ui.Write("\u0001Code_Review\r");
            await Wait(() => ui.Screen.GetText().Contains(" Code_Review ") && !ui.Screen.GetText().Contains(" panes"), ui, "new named tab with an underscore");
            ui.Write("\u001b[<0;39;2M\u001b[<0;39;2m");
            await Wait(() => ui.Screen.GetText().Contains("3 panes"), ui, "previous tab layout");
            ui.Write("\u001b[21~");
            await Wait(() => ui.HasExited, ui, "detach");
            var saved = Assert.Single(new WorkspaceSettingsStore(state).Load().ProjectLayouts).Value;
            Assert.Equal(2, saved.Tabs.Count);
            Assert.Equal(3, saved.ActiveTab.Panes.Count());
            Assert.Equal(SplitAxis.Horizontal, saved.ActiveTab.Root.Axis);
            Assert.Equal(SplitAxis.Vertical, saved.ActiveTab.Root.Second!.Axis);
            Assert.InRange(saved.ActiveTab.Root.Ratio, 0.6, 0.8);
            Assert.Contains(saved.Tabs, t => t.Name == "Code_Review");
            Assert.All((await client.CallAsync(new("list"))).Sessions!, session => Assert.False(session.HasExited));

            using var reopened = Launch("layout-reopen", root, state);
            await Wait(() => reopened.Screen.GetText().Contains("3 panes") && reopened.Screen.GetText().Contains("CHARLIE_TARGET"), reopened, "restored nested layout");
            await Command(reopened, "Terminal: Close pane");
            await Wait(() => reopened.Screen.GetText().Contains("2 panes"), reopened, "pane close");
            Assert.Equal(3, (await client.CallAsync(new("list"))).Sessions!.Length);
            reopened.Write("\u001b[21~");
            await Wait(() => reopened.HasExited, reopened, "reopened detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }

    /// <summary>
    /// 按 F9 打开命令面板并等待出现 <c>COMMANDS</c>，输入命令名进行过滤，等待屏幕上出现该命令（而不是固定延时，
    /// 避免并行负载下界面来不及重绘），再稍候让过滤结果选中该项后按 Enter 执行。
    /// </summary>
    /// <remarks>
    /// F9 最多按 3 次，每次等待 5 秒：刚关闭的对话框可能还位于最上层（此时工作台不处理快捷键），
    /// 或者负载高时之前单独按下的 Esc 与随后的 F9 被输入解析器合在一起读成 Alt+F9，按键因此丢失。
    /// 命令面板打开后它是模态对话框，多余的 F9 不会再打开第二个面板。按 F9 之前先等上一次的面板从屏幕上消失，以免把旧标题当成新面板。
    /// </remarks>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <param name="command">命令面板中的完整命令名，例如 <c>Terminal: Split right</c>。</param>
    /// <returns>表示操作过程的任务。</returns>
    internal static async Task Command(ITerminalSession ui, string command)
    {
        // 上一次的命令面板关闭后，屏幕可能还没重绘掉它的标题；不先等它消失，就会把旧标题当成新面板，命令文字随后被键入终端。
        await Wait(() => !ui.Screen.GetText().Contains("COMMANDS"), ui, "previous palette closed");
        await Press(ui, "\u001b[20~", () => ui.Screen.GetText().Contains("COMMANDS"));
        await Wait(() => ui.Screen.GetText().Contains("COMMANDS"), ui, "palette");
        ui.Write(command);
        await Wait(() => ui.Screen.GetText().Contains(command), ui, "palette filter: " + command);
        await Task.Delay(100);
        ui.Write("\r");
    }

    /// <summary>
    /// 按 F10 分离工作区界面并等待进程退出；按键丢失（原因见 <see cref="Command"/>）时最多重按 3 次。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <returns>表示操作过程的任务。</returns>
    internal static async Task Detach(ITerminalSession ui)
    {
        await Press(ui, "\u001b[21~", () => ui.HasExited);
        await Wait(() => ui.HasExited, ui, "detach");
    }

    /// <summary>
    /// 发送一个按键序列，直到条件成立：最多发送 3 次，每次之后最多等待 5 秒；界面进程已退出时停止。
    /// 只用于重复发送也无害的按键（打开模态对话框、分离等）。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <param name="keys">要发送的按键序列。</param>
    /// <param name="succeeded">按键已生效的判断条件。</param>
    /// <returns>表示操作过程的任务；条件最终是否成立由调用方随后的等待判断。</returns>
    internal static async Task Press(ITerminalSession ui, string keys, Func<bool> succeeded)
    {
        for (var attempt = 0; attempt < 3 && !succeeded() && !ui.HasExited; attempt++)
        {
            ui.Write(keys);
            for (var waited = 0; waited < 50 && !succeeded() && !ui.HasExited; waited++) await Task.Delay(100);
        }
    }

    /// <summary>
    /// 在 180×55 的伪终端中启动被测工作区界面（可执行文件见 <see cref="CrewTermBuild.Executable"/>），并传入 <c>--state-dir</c> 与 <c>--project</c>。
    /// </summary>
    /// <param name="id">伪终端会话标识。</param>
    /// <param name="root">仓库根目录，同时作为进程工作目录和 <c>--project</c> 参数。</param>
    /// <param name="state">daemon 状态目录。</param>
    /// <returns>运行界面的伪终端会话。</returns>
    internal static ITerminalSession Launch(string id, string root, string state)
        => TerminalSession.Start(id, $"\"{CrewTermBuild.Executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 180, 55);

    /// <summary>
    /// 界面屏幕上可见的最小 <c>HISTORY_LINE_</c> 行号，即终端面板最上方的历史行。
    /// </summary>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <returns>最小行号；屏幕上没有任何历史行时为 <see cref="int.MaxValue"/>。</returns>
    private static int TopHistoryLine(ITerminalSession ui) =>
        System.Text.RegularExpressions.Regex.Matches(ui.Screen.GetText(), @"HISTORY_LINE_(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(int.MaxValue).Min();

    /// <summary>
    /// 在 15 秒内每 100 毫秒向 daemon 请求一次指定会话的屏幕文本，直到其中包含期望文本；超时则附上界面当前屏幕内容使测试失败。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="id">要检查的会话标识。</param>
    /// <param name="text">期望出现的文本。</param>
    /// <param name="ui">运行工作区界面的伪终端会话，仅用于失败时输出诊断屏幕。</param>
    /// <returns>表示等待过程的任务。</returns>
    internal static async Task WaitForText(DaemonClient client, string id, string text, ITerminalSession ui)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end)
        {
            if ((await client.CallAsync(new("screen", id))).Text?.Contains(text) == true) return;
            await Task.Delay(100);
        }
        Assert.Fail("Terminal did not receive text: " + text + "\n" + ui.Screen.GetText());
    }

    /// <summary>
    /// 在 30 秒内每 100 毫秒检查一次条件，成立即返回；若界面进程已退出（且条件仍不成立）或超时，则以步骤名和界面当前屏幕内容使测试失败。
    /// 上限按多个测试程序集并行、CPU 被占满的情况放宽，条件成立时立即返回，不会拖慢正常运行。
    /// </summary>
    /// <param name="condition">要等待成立的条件。</param>
    /// <param name="ui">运行工作区界面的伪终端会话。</param>
    /// <param name="step">失败信息中描述当前步骤的文字。</param>
    /// <returns>表示等待过程的任务。</returns>
    internal static async Task Wait(Func<bool> condition, ITerminalSession ui, string step)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return;
            if (ui.HasExited) break;
            await Task.Delay(100);
        }
        Assert.Fail(step + "\n" + ui.Screen.GetText());
    }

    /// <summary>
    /// 等待会话的 Shell 打印出提示符（最后一个非空行以 <c>&gt;</c>、<c>$</c> 或 <c>#</c> 结尾），之后才可以向它输入命令。
    /// </summary>
    /// <remarks>
    /// Shell 开始读取命令之前就输入：在 Linux / macOS 上命令会被终端立即回显、输出接在提示符之后；在负载很高的 Windows 运行器上，
    /// 曾出现会话屏幕 30 秒内始终为空的失败。先等提示符可以避免这一竞争，失败信息同时附上会话自己的屏幕与外层界面，
    /// 能分清是 Shell 没有启动还是界面没有刷新。
    /// </remarks>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="id">会话标识。</param>
    /// <param name="ui">外层界面的伪终端会话，仅用于失败时输出诊断屏幕。</param>
    /// <returns>表示等待过程的任务。</returns>
    internal static Task WaitForShellPrompt(DaemonClient client, string id, ITerminalSession ui) =>
        WaitForSessionScreen(client, id, ui, screen =>
            screen.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).LastOrDefault(line => line.Length > 0) is [.., '>' or '$' or '#'],
            "Shell prompt did not appear");

    /// <summary>
    /// 每 100 毫秒读取一次会话的屏幕文本，最长 30 秒，直到条件成立；仍不成立则使测试失败，并附上会话屏幕与外层界面屏幕。
    /// </summary>
    /// <param name="client">连接测试 daemon 的客户端。</param>
    /// <param name="id">会话标识。</param>
    /// <param name="ui">外层界面的伪终端会话，仅用于失败时输出诊断屏幕。</param>
    /// <param name="condition">对会话屏幕文本的判断。</param>
    /// <param name="step">失败信息中描述所等待步骤的文字。</param>
    /// <returns>表示等待过程的任务。</returns>
    internal static async Task WaitForSessionScreen(DaemonClient client, string id, ITerminalSession ui, Func<string, bool> condition, string step)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        var screen = "";
        while (DateTime.UtcNow < end)
        {
            screen = (await client.CallAsync(new("screen", id))).Text ?? "";
            if (condition(screen)) return;
            await Task.Delay(100);
        }
        Assert.Fail($"{step}. Session screen:\n{screen}\nOuter UI:\n{ui.Screen.GetText()}");
    }
}
