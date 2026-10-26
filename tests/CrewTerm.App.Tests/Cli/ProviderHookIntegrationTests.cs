using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.Tests.Cli;

/// <summary>
/// 提供方钩子（Claude / Codex hook）的集成测试：以 <c>--server</c> 模式启动真实的 <c>crewterm.exe</c> daemon 进程，
/// 覆盖 <c>agent-hook</c> 协议的事件到状态映射与原生会话绑定、真实钩子可执行文件对提供方负载的处理、daemon 不存在时钩子的行为，
/// 以及经 ConPTY 启动提供方时注入的钩子配置能被实际调用。
/// </summary>
public sealed class ProviderHookIntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermProviderHookTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermProviderHookTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 以 <c>--server</c> 模式运行的 daemon 进程。
    /// </summary>
    private Process? daemon;

    /// <summary>
    /// 每次访问都新建一个连接到测试 daemon 的客户端。
    /// </summary>
    private DaemonClient Client => new(state);

    /// <summary>
    /// 测试使用的固定原生会话标识（GUID 格式）。
    /// </summary>
    private const string NativeId = "9e254cb3-ac0d-4a65-9446-93c57ee2f0f8";

    /// <summary>
    /// 创建状态目录并以 <c>--server --state-dir</c> 启动 daemon 进程（子进程的 <c>CODEX_HOME</c> 指向状态目录下的 <c>isolated-codex</c>），
    /// 然后在 5 秒内反复 <c>ping</c> 直到得到成功响应；daemon 进程提前退出或超时则抛出最后一次的异常。
    /// </summary>
    /// <returns>表示启动过程的任务。</returns>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(state);
        var start = new ProcessStartInfo(ResolveExecutable()) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = state };
        foreach (var arg in new[] { "--server", "--state-dir", state }) start.ArgumentList.Add(arg);
        // 只隔离子进程的环境：夹具生成的启动配置不得替换用户自己的 Codex notify 设置。
        start.Environment["CODEX_HOME"] = Directory.CreateDirectory(Path.Combine(state, "isolated-codex")).FullName;
        daemon = Process.Start(start)!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { Assert.True((await Client.CallAsync(new("ping"))).Ok); break; }
            catch (Exception error) when (error is TimeoutException or IOException && DateTime.UtcNow < deadline && !daemon.HasExited)
            { await Task.Delay(30); }
        }
    }

    /// <summary>
    /// 验证 <c>claude</c> 钩子事件到 Agent 状态的映射：<c>SessionStart</c> → Idle，<c>UserPromptSubmit</c> → Working，<c>PermissionRequest</c> → Blocked，<c>Stop</c> → Done；
    /// 以新原生标识再次 <c>SessionStart</c> 后会话改绑到新标识，此后携带旧标识的 <c>Stop</c> 请求虽然成功但被忽略（状态仍为 Idle）；
    /// 会话关闭后，即使携带当前标识的 <c>Stop</c> 也不会改变 Exited 状态。
    /// </summary>
    [Fact]
    public async Task HookBindsExactConversationAndIgnoresStaleIdentityOrClosedTerminal()
    {
        var created = await Client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state, Provider: "claude"));
        Assert.True(created.Ok, created.Error);
        // 以 claude 提供方向当前会话发送一次 agent-hook 请求，负载包含钩子事件名和原生会话标识（默认为 NativeId）。
        async Task<DaemonResponse> Hook(string eventName, string nativeId = NativeId) => await Client.CallAsync(new("agent-hook", created.SessionId,
            Provider: "claude", Payload: JsonSerializer.SerializeToElement(new { hook_event_name = eventName, session_id = nativeId })));
        var started = await Hook("SessionStart");
        Assert.True(started.Ok, started.Error);
        Assert.Equal(AgentActivityState.Idle, started.Activity!.State);
        Assert.Equal(AgentActivityState.Working, (await Hook("UserPromptSubmit")).Activity!.State);
        Assert.Equal(AgentActivityState.Blocked, (await Hook("PermissionRequest")).Activity!.State);
        Assert.Equal(AgentActivityState.Done, (await Hook("Stop")).Activity!.State);
        const string next = "04d8cc3b-1a39-4ea7-aa20-f4adfa732ce2";
        Assert.Equal(AgentActivityState.Idle, (await Hook("SessionStart", next)).Activity!.State);
        Assert.True((await Hook("Stop")).Ok);
        Assert.Equal(AgentActivityState.Idle, (await Client.CallAsync(new("agent-state", created.SessionId))).Activity!.State);
        Assert.Equal(next, Assert.Single((await Client.CallAsync(new("session-catalog"))).Sessions!).NativeSessionId);
        await Client.CallAsync(new("close", created.SessionId));
        Assert.True((await Hook("Stop", next)).Ok);
        Assert.Equal(AgentActivityState.Exited, (await Client.CallAsync(new("agent-state", created.SessionId))).Activity!.State);
    }

    /// <summary>
    /// 验证：用真实的 <c>crewterm agent-hook</c> 进程投递提供方负载（claude 经标准输入传 <c>Stop</c> 事件，codex 以命令行参数传 <c>agent-turn-complete</c> 通知）时，
    /// 进程以退出码 0 结束且标准输出和标准错误都为空，会话状态变为来源 <c>hook</c> 的 Done，并绑定负载中的原生会话标识。
    /// </summary>
    /// <param name="provider">提供方名称：<c>claude</c> 或 <c>codex</c>。</param>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ActualHookExecutableConsumesProviderPayloadSilently(string provider)
    {
        var created = await Client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state, Provider: provider));
        var payload = provider == "claude"
            ? JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = NativeId })
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "agent-turn-complete", ["thread-id"] = NativeId });
        var result = await InvokeHookAsync(provider, created.SessionId!, payload, provider == "claude");
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("", result.Output);
        Assert.Equal("", result.Error);
        var activity = (await Client.CallAsync(new("agent-state", created.SessionId))).Activity!;
        Assert.Equal(AgentActivityState.Done, activity.State);
        Assert.Equal("hook", activity.Source);
        Assert.Equal(NativeId, Assert.Single((await Client.CallAsync(new("session-catalog"))).Sessions!).NativeSessionId);
    }

    /// <summary>
    /// 验证：状态目录中没有 daemon 运行时，<c>agent-hook</c> 以退出码 1 结束，不向标准输出写任何内容（不会替提供方做出权限决定），也不会启动 daemon（不存在 <c>server.lock</c>）。
    /// </summary>
    [Fact]
    public async Task MissingDaemonHookNeverStartsServerOrEmitsPermissionDecisions()
    {
        var missingState = Path.Combine(state, "missing-daemon");
        var result = await InvokeHookAsync("claude", "closed", JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = NativeId }), true, missingState);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.False(File.Exists(Path.Combine(missingState, "server.lock")));
    }

    /// <summary>
    /// 验证：以伪造的 <c>claude.cmd</c> / <c>codex.cmd</c> 经 ConPTY 启动会话时，daemon 注入的启动配置（claude 的 <c>--settings</c> 钩子文件与 <c>--session-id</c>，codex 的 <c>notify=</c> 配置）
    /// 能被提供方脚本解析并实际调用钩子程序：钩子进程退出码 0 且没有任何输出，会话状态变为来源 <c>hook</c> 的 Done，记录正确的原生会话标识并且可以恢复。
    /// </summary>
    /// <param name="provider">提供方名称：<c>claude</c> 或 <c>codex</c>。</param>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task RealConPtyLaunchConfigInvokesHookExeAndCapturesNativeState(string provider)
    {
        var fixture = Path.Combine(Directory.CreateDirectory(Path.Combine(state, "fixture tools")).FullName, provider + ".cmd");
        var script = Path.Combine(state, "invoke provider hook.ps1");
        PowerShellFixture.WriteScript(script, FixtureScript);
        File.WriteAllText(fixture, "@echo off\r\n" + PowerShellFixture.CommandLine(script) + " %*\r\n");
        var created = await Client.CallAsync(new("create", Command: "\"" + fixture + "\"", WorkingDirectory: state, Provider: provider));
        Assert.True(created.Ok, created.Error);
        var resultFile = Path.Combine(state, "hook-result.json");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(resultFile) && DateTime.UtcNow < deadline) await Task.Delay(40);
        Assert.True(File.Exists(resultFile), (await Client.CallAsync(new("screen", created.SessionId))).Text);
        using var result = JsonDocument.Parse(File.ReadAllText(resultFile));
        Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("", result.RootElement.GetProperty("output").GetString());
        Assert.Equal("", result.RootElement.GetProperty("error").GetString());
        var session = Assert.Single((await Client.CallAsync(new("session-catalog"))).Sessions!);
        Assert.Equal(AgentActivityState.Done, session.Activity!.State);
        Assert.Equal("hook", session.Activity.Source);
        Assert.Equal(provider == "codex" ? NativeId : result.RootElement.GetProperty("nativeId").GetString(), session.NativeSessionId);
        Assert.True(session.CanResume, session.ResumeReason);
    }

    /// <summary>
    /// 伪造提供方运行的 PowerShell 脚本。claude 模式：从 <c>--settings</c> 指向的配置中取出 <c>Stop</c> 钩子的命令与参数，以 <c>--session-id</c> 的值为原生标识，经标准输入发送 <c>Stop</c> 事件；
    /// codex 模式：从 <c>notify=</c> 配置中解析钩子命令与参数，并把 <c>agent-turn-complete</c> 负载追加为最后一个参数。
    /// 脚本按 CRT 规则拼接参数启动钩子进程，把退出码、输出和原生标识写入状态目录下的 <c>hook-result.json</c>，然后睡眠 60 秒以保持会话存活。
    /// </summary>
    private const string FixtureScript = """
        $ErrorActionPreference='Stop'
        if ($env:CREWTERM_PROVIDER -eq 'claude') {
            $index = [Array]::IndexOf($args, '--settings')
            if ($index -lt 0) { throw 'No launch hook settings supplied' }
            $settings = Get-Content -LiteralPath $args[$index + 1] -Raw | ConvertFrom-Json
            $hook = $settings.hooks.Stop[0].hooks[0]
            $exe = $hook.command
            $hookArgs = @($hook.args)
            $nativeId = $args[[Array]::IndexOf($args, '--session-id') + 1]
            $payload = @{hook_event_name='Stop'; session_id=$nativeId} | ConvertTo-Json -Compress
        } else {
            $config = @($args | Where-Object { $_.StartsWith('notify=') })[0]
            if (!$config) { throw 'No notify launch configuration supplied' }
            $notify = @([regex]::Matches($config, "'([^']*)'") | ForEach-Object { $_.Groups[1].Value })
            $exe = $notify[0]
            $hookArgs = @($notify | Select-Object -Skip 1)
            $nativeId = '9e254cb3-ac0d-4a65-9446-93c57ee2f0f8'
            $payload = @{'type'='agent-turn-complete'; 'thread-id'=$nativeId} | ConvertTo-Json -Compress
            $hookArgs += $payload
        }
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute=$false
        $start.RedirectStandardInput=$true
        $start.RedirectStandardOutput=$true
        $start.RedirectStandardError=$true
        # ProcessStartInfo.ArgumentList 在 Windows PowerShell 5.1（.NET Framework）中不存在，按 CRT 规则拼接参数。
        $start.Arguments = ($hookArgs | ForEach-Object { '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }) -join ' '
        $process = [Diagnostics.Process]::Start($start)
        if ($env:CREWTERM_PROVIDER -eq 'claude') { $process.StandardInput.Write($payload) }
        $process.StandardInput.Close()
        $output=$process.StandardOutput.ReadToEnd()
        $errorOutput=$process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $result=@{exitCode=$process.ExitCode; output=$output; error=$errorOutput; nativeId=$nativeId}
        [IO.File]::WriteAllText((Join-Path $env:CREWTERM_STATE_DIR 'hook-result.json'), ($result | ConvertTo-Json -Compress))
        $process.Dispose()
        Start-Sleep -Seconds 60
        """;

    /// <summary>
    /// 以 <c>--state-dir &lt;目录&gt; agent-hook --provider &lt;提供方&gt; --id &lt;会话&gt;</c> 运行一次真实的钩子可执行文件，负载按需经标准输入写入或作为最后一个命令行参数传入，
    /// 并以 UTF-8 收集输出；8 秒内未结束则杀死进程树并重新抛出超时异常。
    /// </summary>
    /// <param name="provider">提供方名称。</param>
    /// <param name="id">CrewTerm 会话标识。</param>
    /// <param name="payload">提供方发送给钩子的 JSON 负载。</param>
    /// <param name="stdin">为 true 时经标准输入写入负载，为 false 时作为命令行参数传入。</param>
    /// <param name="stateOverride">替代测试状态目录的目录；为 null 时使用测试状态目录。</param>
    /// <returns>退出码、标准输出和标准错误。</returns>
    private async Task<(int ExitCode, string Output, string Error)> InvokeHookAsync(string provider, string id, string payload, bool stdin, string? stateOverride = null)
    {
        var start = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "--state-dir", stateOverride ?? state, "agent-hook", "--provider", provider, "--id", id }) start.ArgumentList.Add(arg);
        if (!stdin) start.ArgumentList.Add(payload);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (stdin) await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        try { await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>
    /// 确定被测可执行文件：优先使用环境变量 <c>CREWTERM_TEST_EXE</c>；否则从测试输出目录向上找到包含 <c>CrewTerm.slnx</c> 的仓库根目录，
    /// 再拼出与测试程序集相同构建配置下的 <c>src\CrewTerm.App\bin\&lt;配置&gt;\net10.0\crewterm.exe</c>。
    /// </summary>
    /// <returns>可执行文件的完整路径。</returns>
    private static string ResolveExecutable()
    {
        if (Environment.GetEnvironmentVariable("CREWTERM_TEST_EXE") is { } published) return published;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrewTerm.slnx"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src", "CrewTerm.App", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "crewterm.exe");
    }

    /// <summary>
    /// 测试结束后清理：daemon 进程仍在运行时先发送 <c>stop</c> 并最多等待 5 秒，未退出则杀死进程树；
    /// 再向 <c>missing-daemon</c> 状态目录发送 <c>stop</c>，确保不遗留意外启动的 daemon；最后在 5 秒内反复尝试删除状态目录。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        if (daemon is { HasExited: false })
        {
            try { await Client.CallAsync(new("stop")); await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { if (!daemon.HasExited) daemon.Kill(true); }
        }
        daemon?.Dispose();
        // 即使出现回归（钩子意外为该目录启动了 daemon），也不能遗留脱离的辅助 daemon 进程。
        try { await new DaemonClient(Path.Combine(state, "missing-daemon")).CallAsync(new("stop")); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Directory.Exists(state))
            try { Directory.Delete(state, true); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
    }
}
