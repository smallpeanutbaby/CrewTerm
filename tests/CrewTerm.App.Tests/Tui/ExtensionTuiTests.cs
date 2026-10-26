using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Automation.Plugins;
using static CrewTerm.App.Tests.Tui.LayoutTuiTests;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 扩展功能（恢复原生会话、本地插件）的端到端界面测试：在伪终端中启动真实的 <c>crewterm</c> 工作区，
/// 连接独立临时状态目录中的真实 daemon，通过命令面板驱动 TUI，并用 <see cref="DaemonClient"/> 和磁盘文件核对实际效果。
/// </summary>
[Collection(TuiCollection.Name)]
public sealed class ExtensionTuiTests
{
    /// <summary>
    /// 验证：绑定了原生会话标识的 <c>codex</c> 会话关闭后，在命令面板 <c>Agent: Resume original conversation</c> 的列表中按 Enter，
    /// 提供方命令以 <c>resume &lt;原生标识&gt;</c> 启动（脚本写出的标识与绑定值一致），且恢复后唯一的存活会话沿用原会话标识。
    /// </summary>
    [Fact]
    public async Task ResumeMenuStartsTheExactSavedNativeConversation()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermExtensionUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        // 夹具沿用真实提供方的可执行文件名（codex）；对于任意命名的包装脚本，
        // 生产环境的提供方适配器有意要求显式提供恢复命令。
        var fixture = TestShell.WriteResumeRecorder(state, "codex", "resumed.txt");
        const string nativeId = "871fcb41-2d23-48cb-9f86-e9d03934cfd1";
        try
        {
            using var ui = Launch("resume-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            var created = await client.CallAsync(new("create", Command: $"\"{fixture}\"", WorkingDirectory: state, Name: "Saved native conversation", Provider: "codex"));
            Assert.True(created.Ok, created.Error);
            Assert.True((await client.CallAsync(new("session-bind", created.SessionId, Provider: "codex", NativeSessionId: nativeId))).Ok);
            Assert.True((await client.CallAsync(new("close", created.SessionId))).Ok);
            await Command(ui, "Agent: Resume original conversation");
            await Wait(() => ui.Screen.GetText().Contains("RESUME CONVERSATION"), ui, "resume list");
            ui.Write("\r");
            await Wait(() => File.Exists(Path.Combine(state, "resumed.txt")), ui, "native resume process");
            Assert.Equal(nativeId, File.ReadAllText(Path.Combine(state, "resumed.txt")).Trim());
            // daemon 先启动恢复的进程、写完状态文件后才把会话登记到列表中，记录文件可能早于登记出现：等会话出现在列表中再断言。
            var listed = (await client.CallAsync(new("list"))).Sessions!;
            for (var end = DateTime.UtcNow.AddSeconds(15); listed.Length == 0 && DateTime.UtcNow < end; listed = (await client.CallAsync(new("list"))).Sessions!)
                await Task.Delay(100);
            Assert.Equal(created.SessionId, Assert.Single(listed).Id);
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
    /// 验证：通过命令面板安装本地插件目录后插件处于禁用状态；启用后执行插件命令会在 <c>PLUGIN OUTPUT</c> 中显示真实输出，脚本收到的负载中 <c>projectRoot</c> 为当前项目根目录；
    /// 再次切换为禁用并移除登记后插件列表为空，而插件脚本文件仍然保留。
    /// </summary>
    [Fact]
    public async Task LocalPluginCanBeInstalledEnabledRunAndRemovedThroughUi()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermExtensionUi", Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(state, "local plugin")).FullName;
        var script = Path.Combine(directory, "proof.ps1");
        PowerShellFixture.WriteScript(script, "$json = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText((Join-Path $pwd 'proof.json'), $json); Write-Output 'UI_PLUGIN_EXECUTED'");
        var manifest = new LocalPluginManifest("ui-fixture", "Fixture plugin", "1.0.0",
            [new LocalPluginCommand("proof", "Write proof", PowerShellFixture.Executable, PowerShellFixture.FileArguments(script))]);
        File.WriteAllText(Path.Combine(directory, "crewterm-plugin.json"), JsonSerializer.Serialize(manifest));
        var client = new DaemonClient(state);
        try
        {
            using var ui = Launch("plugin-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("F10 Detach"), ui, "workspace ready");
            await Command(ui, "Plugins: Install local folder");
            await Wait(() => ui.Screen.GetText().Contains("Local plugin directory"), ui, "plugin folder prompt");
            ui.Write(directory + "\r");
            await Wait(() => ui.Screen.GetText().Contains("Installed disabled"), ui, "plugin registered");
            Assert.False(Assert.Single((await client.CallAsync(new("plugin-list"))).Plugins!).Enabled);
            await Command(ui, "Plugins: Enable / disable");
            await Wait(() => ui.Screen.GetText().Contains("LOCAL PLUGINS"), ui, "plugin toggle list");
            ui.Write("\r");
            await Wait(() => ui.Screen.GetText().Contains("Plugin enabled"), ui, "plugin enabled");
            await Command(ui, "Plugin: Fixture plugin / Write proof");
            await Wait(() => ui.Screen.GetText().Contains("PLUGIN OUTPUT") && ui.Screen.GetText().Contains("UI_PLUGIN_EXECUTED"), ui, "actual plugin output");
            using var proof = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "proof.json")));
            Assert.Equal(root, proof.RootElement.GetProperty("payload").GetProperty("projectRoot").GetString());
            ui.Write("\u001b"); await Task.Delay(200);
            await Command(ui, "Plugins: Enable / disable");
            await Wait(() => ui.Screen.GetText().Contains("LOCAL PLUGINS"), ui, "disable list");
            ui.Write("\r"); await Wait(() => ui.Screen.GetText().Contains("Plugin disabled"), ui, "plugin disabled");
            await Command(ui, "Plugins: Remove registration");
            await Wait(() => ui.Screen.GetText().Contains("Remove local plugin"), ui, "remove list");
            ui.Write("\r"); await Wait(() => ui.Screen.GetText().Contains("Registration removed"), ui, "plugin removed");
            Assert.Empty((await client.CallAsync(new("plugin-list"))).Plugins!);
            Assert.True(File.Exists(script));
            ui.Write("\u001b[21~"); await Wait(() => ui.HasExited, ui, "detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await TestStateDirectory.DeleteAsync(state);
        }
    }
}
