using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Protocol;
using CrewTerm.App.Tests.Fixtures;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.App.Tests.Cli;

/// <summary>
/// 以真实进程运行 <c>crewterm</c> 可执行文件 的端到端测试：每条命令都通过 <c>--state-dir</c> 指向本测试独占的临时状态目录，
/// 并经由真实命名管道 daemon 执行，覆盖 Agent 状态上报与等待、通知确认、定向消息与交接、保存会话的绑定/恢复/重启/遗忘，以及本地插件的安装、启用、运行与移除。
/// </summary>
public sealed class AgentCliIntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的临时目录（系统临时目录下的 <c>CrewTermAgentCliTests\&lt;GUID&gt;</c>），
    /// 同时用作 daemon 状态目录、子进程工作目录和新建会话的项目目录。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermAgentCliTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 被测 <c>crewterm</c> 可执行文件 的完整路径。
    /// </summary>
    private readonly string executable = CrewTermBuild.Executable;

    /// <summary>
    /// 在每个测试开始前创建临时状态目录。
    /// </summary>
    /// <returns>已完成的任务。</returns>
    public Task InitializeAsync() { Directory.CreateDirectory(state); return Task.CompletedTask; }

    /// <summary>
    /// 验证：<c>agent-report</c> 以来源 <c>hook</c> 上报 <c>done</c> 后，<c>agent-state</c> 返回该状态和来源，并生成一条未读通知；
    /// 对该通知执行 <c>notification-read</c> 后，Agent 状态回到 <see cref="AgentActivityState.Idle"/>，会话列表中的未读计数归零。
    /// </summary>
    [Fact]
    public async Task ExecutableReportsStateAndExplicitlyAcknowledgesNotification()
    {
        var id = await CreateAsync();
        var reported = Response(await RunAsync("agent-report", id, "--state", "done", "--source", "hook", "--reason", "Turn finished"));
        Assert.Equal(AgentActivityState.Done, reported.Activity!.State);
        Assert.Equal("hook", Response(await RunAsync("agent-state", id)).Activity!.Source);
        var notice = Assert.Single(Response(await RunAsync("notifications", id)).Notifications!);
        Assert.False(notice.Read);
        Response(await RunAsync("notification-read", notice.Id));
        Assert.Equal(AgentActivityState.Idle, Response(await RunAsync("agent-state", id)).Activity!.State);
        Assert.Equal(0, Assert.Single(Response(await RunAsync("sessions")).Sessions!).UnreadCount);
    }

    /// <summary>
    /// 验证：<c>message-send</c> 只把消息存入 <c>--to</c> 指定会话的收件列表（发送方列表为空），且不写入终端（<c>Delivered</c> 为 false），
    /// <c>message-ack</c> 后该消息标记为已确认；<c>agent-handoff</c> 返回 <c>Delivered</c> 为 true，交接文本作为输入写入目标会话，5 秒内可在 <c>read</c> 输出中看到标记。
    /// </summary>
    [Fact]
    public async Task ExecutableDeliversDirectedMessageAndHandoffWithObservableOutput()
    {
        var from = await CreateAsync();
        var to = await CreateAsync();
        var message = Response(await RunAsync("message-send", "--from", from, "--to", to, "--text", "Review patch"));
        Assert.False(message.Delivered);
        Assert.Empty(Response(await RunAsync("message-list", from)).Messages!);
        Assert.Single(Response(await RunAsync("message-list", to)).Messages!);
        Response(await RunAsync("message-ack", to, "--message", message.Message!.Id));
        Assert.True(Assert.Single(Response(await RunAsync("message-list", to)).Messages!).Acknowledged);
        var handoff = Response(await RunAsync("agent-handoff", "--from", from, "--to", to, "--text", "echo CLI_HANDOFF_MARKER"));
        Assert.True(handoff.Delivered);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string output;
        do { output = (await RunAsync("read", to)).Output; if (!output.Contains("CLI_HANDOFF_MARKER")) await Task.Delay(50); }
        while (!output.Contains("CLI_HANDOFF_MARKER") && DateTime.UtcNow < deadline);
        Assert.Contains("CLI_HANDOFF_MARKER", output);
    }

    /// <summary>
    /// 验证：<c>agent-wait</c> 在 80 毫秒内等不到 <c>blocked</c> 时以退出码 1 失败并在标准错误中报告 <c>Timed out</c>；
    /// 另一个进程在等待期间上报 <c>done</c> 时，正在进行的 <c>agent-wait</c> 能观察到并返回 <see cref="AgentActivityState.Done"/>。
    /// </summary>
    [Fact]
    public async Task ExecutableWaitReturnsTimeoutFailureAndCanObserveConcurrentReport()
    {
        var id = await CreateAsync();
        var timeout = await RunAsync("agent-wait", id, "--state", "blocked", "--timeout-ms", "80");
        Assert.Equal(1, timeout.ExitCode);
        Assert.Contains("Timed out", timeout.Error);
        var waiting = RunAsync("agent-wait", id, "--state", "done", "--timeout-ms", "2000");
        Response(await RunAsync("agent-report", id, "--state", "done", "--reason", "Complete"));
        Assert.Equal(AgentActivityState.Done, Response(await waiting).Activity!.State);
    }

    /// <summary>
    /// 验证：<c>agent-report</c> 使用未知状态 <c>flying</c> 时以退出码 1 失败并报告 <c>Unknown Agent state</c>，Agent 状态仍为 <see cref="AgentActivityState.Unknown"/>。
    /// </summary>
    [Fact]
    public async Task ExecutableRejectsUnknownStateWithoutMutatingAgent()
    {
        var id = await CreateAsync();
        var result = await RunAsync("agent-report", id, "--state", "flying");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown Agent state", result.Error);
        Assert.Equal(AgentActivityState.Unknown, Response(await RunAsync("agent-state", id)).Activity!.State);
    }

    /// <summary>
    /// 验证：<c>message-send</c> 缺少 <c>--to</c> 时以退出码 1 失败并报告 <c>Required: --to</c>，不会自行挑选接收方。
    /// </summary>
    [Fact]
    public async Task ExecutableRequiresAnExplicitMessageRecipient()
    {
        var id = await CreateAsync();
        var result = await RunAsync("message-send", "--from", id, "--text", "Do not pick another Agent");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Required: --to", result.Error);
    }

    /// <summary>
    /// 验证保存会话的完整生命周期：<c>session-bind</c> 绑定原生会话标识与恢复命令后关闭会话，<c>sessions --all true</c> 列出一条可恢复的休眠会话并保留原生标识；
    /// <c>session-resume</c> 以原会话标识运行恢复命令（输出中出现标记），关闭后 <c>session-restart</c> 仍复用同一标识，最后 <c>session-forget</c> 使目录清空。
    /// </summary>
    [Fact]
    public async Task ExecutableBindsResumesRestartsAndForgetsAnExplicitSavedSession()
    {
        var id = await CreateAsync();
        Response(await RunAsync("session-bind", id, "--provider", "custom", "--native-id", "native-original-42",
            "--resume-command", TestShell.Echo("CLI_NATIVE_RESUME_MARKER")));
        Response(await RunAsync("close", id));
        var saved = Assert.Single(Response(await RunAsync("sessions", "--all", "true")).Sessions!);
        Assert.True(saved.IsDormant);
        Assert.True(saved.CanResume);
        Assert.Equal("native-original-42", saved.NativeSessionId);
        Assert.Equal(id, Response(await RunAsync("session-resume", id)).SessionId);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string output;
        do { output = (await RunAsync("read", id)).Output; if (!output.Contains("CLI_NATIVE_RESUME_MARKER")) await Task.Delay(50); }
        while (!output.Contains("CLI_NATIVE_RESUME_MARKER") && DateTime.UtcNow < deadline);
        Assert.Contains("CLI_NATIVE_RESUME_MARKER", output);
        Response(await RunAsync("close", id));
        Assert.Equal(id, Response(await RunAsync("session-restart", id)).SessionId);
        Response(await RunAsync("close", id));
        Response(await RunAsync("session-forget", id));
        Assert.Empty(Response(await RunAsync("sessions", "--all", "true")).Sessions!);
    }

    /// <summary>
    /// 验证本地插件的完整生命周期：从含空格路径的目录安装后默认禁用且出现在 <c>plugins</c> 列表中，禁用时 <c>plugin-run</c> 以退出码 1 失败；
    /// 启用后 <c>plugin-run</c> 把 <c>--json</c> 负载经标准输入交给回显脚本并成功返回标记，<c>plugin-event</c> 触发对应钩子成功，<c>plugin-history</c> 有记录；
    /// <c>plugin-remove</c> 后列表为空，但插件源目录仍然保留。
    /// </summary>
    [Fact]
    public async Task ExecutableInstallsEnablesRunsAndRemovesLocalPlugin()
    {
        var directory = Directory.CreateDirectory(Path.Combine(state, "local plugin")).FullName;
        var script = Path.Combine(directory, "echo.ps1");
        PowerShellFixture.WriteScript(script, "[Console]::In.ReadToEnd() | Write-Output");
        var manifest = new LocalPluginManifest("cli-fixture", "CLI fixture", "1.0.0",
            [new LocalPluginCommand("echo", "Echo JSON", PowerShellFixture.Executable, PowerShellFixture.FileArguments(script))],
            [new LocalPluginHook("manual.test", "echo")]);
        File.WriteAllText(Path.Combine(directory, "crewterm-plugin.json"), JsonSerializer.Serialize(manifest));
        var installed = Response(await RunAsync("plugin-install", "--path", directory));
        Assert.False(installed.Plugin!.Enabled);
        Assert.Single(Response(await RunAsync("plugins")).Plugins!);
        Assert.Equal(1, (await RunAsync("plugin-run", "cli-fixture", "--command", "echo")).ExitCode);
        Response(await RunAsync("plugin-enable", "cli-fixture", "--enabled", "true"));
        var result = Response(await RunAsync("plugin-run", "cli-fixture", "--command", "echo", "--json", "{\"text\":\"CLI_PLUGIN_MARKER\"}"));
        Assert.True(result.PluginResult!.Success);
        Assert.Contains("CLI_PLUGIN_MARKER", result.PluginResult.Output);
        Assert.True(Assert.Single(Response(await RunAsync("plugin-event", "--event", "manual.test", "--json", "{}")).PluginResults!).Success);
        Assert.NotEmpty(Response(await RunAsync("plugin-history")).PluginResults!);
        Response(await RunAsync("plugin-remove", "cli-fixture"));
        Assert.Empty(Response(await RunAsync("plugins")).Plugins!);
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// 通过 <c>new</c> 命令在临时状态目录中启动一个运行交互式 Shell（<see cref="TestShell.Interactive"/>）的会话（必要时由命令行自动启动 daemon）。
    /// </summary>
    /// <returns>新会话的标识。</returns>
    private async Task<string> CreateAsync() => Response(await RunAsync("new", "--command", TestShell.Interactive, "--project", state)).SessionId!;

    /// <summary>
    /// 以临时状态目录为工作目录、并在参数前加上 <c>--state-dir</c> 运行一次被测可执行文件，以 UTF-8 收集标准输出和标准错误；
    /// 20 秒内未结束则杀死整个进程树并重新抛出超时异常。
    /// </summary>
    /// <param name="arguments">子命令及其参数。</param>
    /// <returns>退出码与输出。</returns>
    private async Task<CliResult> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = state, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--state-dir"); start.ArgumentList.Add(state);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return new(process.ExitCode, await output, await error);
    }

    /// <summary>
    /// 断言命令以退出码 0 成功，把标准输出反序列化为 <see cref="DaemonResponse"/>，并断言其 <c>Ok</c> 为 true。
    /// </summary>
    /// <param name="result">命令运行结果。</param>
    /// <returns>反序列化后的 daemon 响应。</returns>
    private static DaemonResponse Response(CliResult result)
    {
        Assert.True(result.ExitCode == 0, result.Error + "\n" + result.Output);
        var response = JsonSerializer.Deserialize<DaemonResponse>(result.Output)!;
        Assert.True(response.Ok, response.Error);
        return response;
    }

    /// <summary>
    /// 测试结束后清理：若状态目录中仍有 <c>server.lock</c> 则先执行 <c>stop</c> 关闭 daemon，
    /// 再在 10 秒内反复尝试递归删除临时目录（遇到 <see cref="IOException"/> 时每 50 毫秒重试，超时后抛出）。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        if (File.Exists(Path.Combine(state, "server.lock"))) Response(await RunAsync("stop"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(state))
        {
            try { Directory.Delete(state, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline) { await Task.Delay(50); }
        }
    }
}
