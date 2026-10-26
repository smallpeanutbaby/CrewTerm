using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Server;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// 本地插件协议的 daemon 集成测试：在测试进程内运行真实的 <see cref="DaemonHost"/>，插件命令以真实 PowerShell 进程执行并通过标准输入接收 JSON 信封，
/// 覆盖安装、启用、运行与移除，Agent 状态、会话生命周期和消息事件钩子，以及插件失败或超时时 daemon 的可用性。
/// </summary>
public sealed class DaemonPluginProtocolTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermPluginProtocolTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermPluginProtocolTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于停止 daemon 服务循环的取消源。
    /// </summary>
    private readonly CancellationTokenSource stop = new();

    /// <summary>
    /// 当前运行的 daemon 宿主。
    /// </summary>
    private DaemonHost? host;

    /// <summary>
    /// <see cref="DaemonHost.RunAsync"/> 返回的服务循环任务。
    /// </summary>
    private Task? server;

    /// <summary>
    /// 每次访问都新建一个连接到测试 daemon 的客户端。
    /// </summary>
    private DaemonClient Client => new(state);

    /// <summary>
    /// 创建 daemon 宿主并在后台运行其服务循环，再用 <c>ping</c> 确认 daemon 已能响应。
    /// </summary>
    /// <returns>表示启动过程的任务。</returns>
    public async Task InitializeAsync()
    {
        host = new DaemonHost(state); server = host.RunAsync(stop.Token);
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 验证：新安装的插件默认禁用，禁用时 <c>plugin-run</c> 失败；启用后命令以真实进程运行，退出码 0、输出含 <c>PLUGIN_OK</c>，
    /// 并经标准输入收到 JSON 信封，其中 <c>payload.text</c> 原样保留含空格的文本；<c>plugin-remove</c> 后插件列表为空，但插件源目录仍然保留。
    /// </summary>
    [Fact]
    public async Task LocalPluginInstallEnableRunAndRemoveUseRealProcessAndJsonStdin()
    {
        var directory = CreatePlugin("$json = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText((Join-Path $pwd 'received.json'), $json); Write-Output 'PLUGIN_OK'");
        var installed = await Client.CallAsync(new("plugin-install", Path: directory));
        Assert.True(installed.Ok, installed.Error);
        Assert.False(installed.Plugin!.Enabled);
        var disabled = await Client.CallAsync(new("plugin-run", PluginId: "fixture", PluginCommand: "record"));
        Assert.False(disabled.Ok);
        Assert.True((await Client.CallAsync(new("plugin-enable", PluginId: "fixture", Enabled: true))).Ok);
        var payload = JsonSerializer.SerializeToElement(new { text = "literal message with spaces" });
        var run = await Client.CallAsync(new("plugin-run", PluginId: "fixture", PluginCommand: "record", Payload: payload));
        Assert.True(run.Ok, run.Error);
        Assert.Equal(0, run.PluginResult!.ExitCode);
        Assert.Contains("PLUGIN_OK", run.PluginResult.Output);
        using var envelope = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "received.json")));
        Assert.Equal("literal message with spaces", envelope.RootElement.GetProperty("payload").GetProperty("text").GetString());
        Assert.True((await Client.CallAsync(new("plugin-remove", PluginId: "fixture"))).Ok);
        Assert.Empty((await Client.CallAsync(new("plugin-list"))).Plugins!);
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// 验证：已启用并订阅 <c>agent.state</c> 的插件，在会话显式上报 <c>done</c> 后 8 秒内被调用，收到的信封中 <c>eventName</c> 为 <c>agent.state</c>，
    /// <c>payload.sessionId</c> 为该会话的标识。
    /// </summary>
    [Fact]
    public async Task ExplicitStateTransitionInvokesEnabledLocalEventHook()
    {
        var directory = CreatePlugin("$json = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText((Join-Path $pwd 'received.json'), $json)", "agent.state");
        Assert.True((await Client.CallAsync(new("plugin-install", Path: directory, Enabled: true))).Ok);
        var created = await Client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
        Assert.True((await Client.CallAsync(new("agent-report", created.SessionId, State: AgentActivityState.Done, Reason: "Finished work"))).Ok);
        var path = Path.Combine(directory, "received.json");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        string? text = null;
        while (DateTime.UtcNow < deadline)
        {
            try { if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (IOException) { }
            if (text?.Contains("Finished work", StringComparison.Ordinal) == true) break;
            await Task.Delay(50);
        }
        Assert.NotNull(text);
        using var envelope = JsonDocument.Parse(text);
        Assert.Equal("agent.state", envelope.RootElement.GetProperty("eventName").GetString());
        Assert.Equal(created.SessionId, envelope.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
    }

    /// <summary>
    /// 验证：超时设为 1 秒的插件命令运行一个睡眠 30 秒的脚本时，daemon 在运行期间仍响应 <c>ping</c>；该调用最终失败并标记 <c>TimedOut</c>。
    /// 另一个以退出码 9 失败的插件同样只返回失败结果（不是超时），两次失败之后 daemon 依然可用。
    /// </summary>
    [Fact]
    public async Task FailedPluginAndTimedOutPluginDoNotStopDaemon()
    {
        var directory = CreatePlugin("[Console]::In.ReadToEnd() | Out-Null; Start-Sleep -Seconds 30", timeout: 1);
        Assert.True((await Client.CallAsync(new("plugin-install", Path: directory, Enabled: true))).Ok);
        var run = Client.CallAsync(new("plugin-run", PluginId: "fixture", PluginCommand: "record"));
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
        var result = await run;
        Assert.False(result.Ok);
        Assert.True(result.PluginResult!.TimedOut);
        var failing = CreatePlugin("[Console]::In.ReadToEnd() | Out-Null; exit 9", id: "failing");
        Assert.True((await Client.CallAsync(new("plugin-install", Path: failing, Enabled: true))).Ok);
        var failed = await Client.CallAsync(new("plugin-run", PluginId: "failing", PluginCommand: "record"));
        Assert.False(failed.Ok);
        Assert.Equal(9, failed.PluginResult!.ExitCode);
        Assert.False(failed.PluginResult.TimedOut);
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 验证：订阅了指定事件的已启用插件，在对应动作（创建会话、关闭会话、向另一会话发送消息）发生后 8 秒内收到 <c>eventName</c> 与之相同的信封；
    /// 订阅同一事件但未启用的插件不会被执行（其目录中不会出现 <c>received.json</c>）。
    /// </summary>
    /// <param name="eventName">插件订阅并期望收到的事件名。</param>
    [Theory]
    [InlineData("session.created")]
    [InlineData("session.exited")]
    [InlineData("message.received")]
    public async Task LifecycleAndMessageEventsReachOnlyEnabledLocalHooks(string eventName)
    {
        var directory = CreatePlugin("$json = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText((Join-Path $pwd 'received.json'), $json)", eventName);
        Assert.True((await Client.CallAsync(new("plugin-install", Path: directory, Enabled: true))).Ok);
        var disabled = CreatePlugin("$json = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText((Join-Path $pwd 'received.json'), $json)", eventName, id: "disabled");
        Assert.True((await Client.CallAsync(new("plugin-install", Path: disabled, Enabled: false))).Ok);
        var created = await Client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
        if (eventName == "session.exited") await Client.CallAsync(new("close", created.SessionId));
        if (eventName == "message.received")
        {
            var target = await Client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
            await Client.CallAsync(new("message-send", FromSessionId: created.SessionId, TargetSessionId: target.SessionId, Text: "Event message"));
        }
        var path = Path.Combine(directory, "received.json");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        string? text = null;
        while (DateTime.UtcNow < deadline)
        {
            try { if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (IOException) { }
            if (text?.Contains(eventName, StringComparison.Ordinal) == true) break;
            await Task.Delay(50);
        }
        Assert.NotNull(text);
        using var envelope = JsonDocument.Parse(text);
        Assert.Equal(eventName, envelope.RootElement.GetProperty("eventName").GetString());
        Assert.False(File.Exists(Path.Combine(disabled, "received.json")));
    }

    /// <summary>
    /// 验证：通过 <c>plugin-event</c> 显式触发事件且钩子脚本抛出异常时，响应为失败并包含唯一一条 <c>Success</c> 为 false 的插件结果，daemon 控制接口仍然可用。
    /// </summary>
    [Fact]
    public async Task ExplicitEventFailureIsReportedAndControlApiRemainsAvailable()
    {
        var directory = CreatePlugin("throw 'expected fixture failure'", "custom.event");
        Assert.True((await Client.CallAsync(new("plugin-install", Path: directory, Enabled: true))).Ok);
        var result = await Client.CallAsync(new("plugin-event", EventName: "custom.event", Payload: JsonSerializer.SerializeToElement(new { value = 42 })));
        Assert.False(result.Ok);
        Assert.False(Assert.Single(result.PluginResults!).Success);
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 在状态目录下的 <c>plugin source</c>（或 <c>plugin source &lt;标识&gt;</c>）目录中写入 PowerShell 脚本 <c>record.ps1</c> 和清单 <c>crewterm-plugin.json</c>：
    /// 插件含一条用 PowerShell 执行该脚本的命令 <c>record</c>；指定事件名时再添加一个把该事件绑定到 <c>record</c> 的钩子。
    /// </summary>
    /// <param name="source">脚本内容。</param>
    /// <param name="eventName">要订阅的事件名；为 null 时不添加钩子。</param>
    /// <param name="timeout"><c>record</c> 命令的超时秒数。</param>
    /// <param name="id">插件标识；默认 <c>fixture</c>，其他标识使用独立的插件目录。</param>
    /// <returns>插件目录的完整路径。</returns>
    private string CreatePlugin(string source, string? eventName = null, int timeout = 10, string id = "fixture")
    {
        var directory = Directory.CreateDirectory(Path.Combine(state, id == "fixture" ? "plugin source" : "plugin source " + id)).FullName;
        var script = Path.Combine(directory, "record.ps1");
        PowerShellFixture.WriteScript(script, source);
        var manifest = new LocalPluginManifest(id, "Protocol fixture", "1.0.0",
            [new LocalPluginCommand("record", "Record event", PowerShellFixture.Executable, PowerShellFixture.FileArguments(script), TimeoutSeconds: timeout)],
            eventName is null ? null : [new LocalPluginHook(eventName, "record")]);
        File.WriteAllText(Path.Combine(directory, "crewterm-plugin.json"), JsonSerializer.Serialize(manifest));
        return directory;
    }

    /// <summary>
    /// 停止 daemon 服务循环并等待其结束，释放宿主与取消源，最后删除状态目录。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        await stop.CancelAsync(); if (server is not null) await server; host?.Dispose(); stop.Dispose();
        await TestStateDirectory.DeleteAsync(state);
    }
}
