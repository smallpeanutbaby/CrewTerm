using System.Text.Json;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Hooks;

namespace CrewTerm.Automation.Tests.Hooks;

/// <summary>
/// 覆盖 <see cref="AgentHookEvent.Parse"/> 对 Claude hook 与 Codex notify 负载的解析，以及
/// <see cref="AgentHookLaunchService.Prepare"/> 为单次启动接入回调、并在用户已有配置或命令无法可靠解析时保持原命令的规则。
/// 解析测试为纯内存单元测试；启动测试在系统临时目录下独立的状态目录与 Codex 配置目录中读写真实文件，不触碰用户的全局配置。
/// </summary>
public sealed class AgentHookTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的状态目录（以随机 GUID 命名）；不预先创建，由被测代码或测试按需创建。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermHookTests", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 测试负载中使用的原生对话标识，符合 Provider 要求的精确 GUID 格式。
    /// </summary>
    private const string NativeId = "9e254cb3-ac0d-4a65-9446-93c57ee2f0f8";

    /// <summary>
    /// 验证：每个已知的 Claude hook 事件都映射到预期的活动状态，携带负载中精确的原生对话标识，原因文本包含事件名，
    /// 且只有 <c>SessionStart</c> 允许重新绑定会话。
    /// </summary>
    /// <param name="eventName">Claude hook 事件名（<c>hook_event_name</c>）。</param>
    /// <param name="expected">该事件应映射到的活动状态。</param>
    [Theory]
    [InlineData("SessionStart", AgentActivityState.Idle)]
    [InlineData("UserPromptSubmit", AgentActivityState.Working)]
    [InlineData("PermissionRequest", AgentActivityState.Blocked)]
    [InlineData("Stop", AgentActivityState.Done)]
    [InlineData("StopFailure", AgentActivityState.Blocked)]
    [InlineData("SessionEnd", AgentActivityState.Idle)]
    public void KnownClaudeEventsCarryExactIdentityAndEvidence(string eventName, AgentActivityState expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { hook_event_name = eventName, session_id = NativeId });
        var parsed = AgentHookEvent.Parse("claude", payload)!;
        Assert.Equal(NativeId, parsed.NativeSessionId);
        Assert.Equal(expected, parsed.State);
        Assert.Contains(eventName, parsed.Reason);
        Assert.Equal(eventName == "SessionStart", parsed.CanRebind);
    }

    /// <summary>
    /// 验证：Claude 的 <c>Notification</c> 事件按通知类型区分——<c>permission_prompt</c> 为等待决定，
    /// <c>idle_prompt</c> 为空闲，而不会被误当作已完成。
    /// </summary>
    /// <param name="notification">通知类型（<c>notification_type</c>）。</param>
    /// <param name="expected">该通知类型应映射到的活动状态。</param>
    [Theory]
    [InlineData("permission_prompt", AgentActivityState.Blocked)]
    [InlineData("idle_prompt", AgentActivityState.Idle)]
    public void ClaudeNotificationDoesNotConfuseIdleWithCompletion(string notification, AgentActivityState expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { hook_event_name = "Notification", session_id = NativeId, notification_type = notification });
        Assert.Equal(expected, AgentHookEvent.Parse("claude", payload)!.State);
    }

    /// <summary>
    /// 验证：Claude 的 <c>Stop</c> 事件负载中带有非空的后台任务或定时任务数组时，状态仍为
    /// <see cref="AgentActivityState.Working"/>，不会声称全部工作已完成。
    /// </summary>
    /// <param name="key">表示待处理后台工作的字段名。</param>
    [Theory]
    [InlineData("background_tasks")]
    [InlineData("session_crons")]
    public void ClaudeStopWithPendingBackgroundWorkDoesNotClaimAllWorkFinished(string key)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["hook_event_name"] = "Stop", ["session_id"] = NativeId, [key] = new[] { new { id = "pending" } } });
        Assert.Equal(AgentActivityState.Working, AgentHookEvent.Parse("claude", payload)!.State);
    }

    /// <summary>
    /// 验证：Codex 的 <c>agent-turn-complete</c> 通知映射为 <see cref="AgentActivityState.Done"/>，原生标识取自 <c>thread-id</c>，
    /// 而 <c>last-assistant-message</c> 中的助手回复内容不会出现在原因文本中。
    /// </summary>
    [Fact]
    public void CodexTurnCompleteCarriesNativeThreadIdWithoutReadingPrivateAssistantText()
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["type"] = "agent-turn-complete", ["thread-id"] = NativeId, ["last-assistant-message"] = "private source contents" });
        var parsed = AgentHookEvent.Parse("codex", payload)!;
        Assert.Equal(NativeId, parsed.NativeSessionId);
        Assert.Equal(AgentActivityState.Done, parsed.State);
        Assert.DoesNotContain("private source contents", parsed.Reason);
    }

    /// <summary>
    /// 验证：Claude 与 Codex 的未知事件类型都被忽略（返回 <see langword="null"/>）；已知事件携带
    /// <c>--last</c> 这类非精确格式的会话标识时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    [Fact]
    public void UnknownEventsAreIgnoredAndMalformedIdentityIsRejected()
    {
        Assert.Null(AgentHookEvent.Parse("claude", JsonSerializer.SerializeToElement(new { hook_event_name = "FutureEvent" })));
        Assert.Null(AgentHookEvent.Parse("codex", JsonSerializer.SerializeToElement(new { type = "future-event" })));
        Assert.Throws<ArgumentException>(() => AgentHookEvent.Parse("claude", JsonSerializer.SerializeToElement(new { hook_event_name = "Stop", session_id = "--last" })));
    }

    /// <summary>
    /// 验证：Claude 启动被接入时，命令追加 <c>--settings</c> 指向本次启动专用的设置文件；文件中 7 个生命周期事件的 hook
    /// 都以 exec 形式（<c>command</c> 为含空格的 CrewTerm 可执行文件路径，<c>args</c> 数组中含 <c>agent-hook</c> 与会话标识）调用，
    /// 超时为 3 秒，无需修改 Claude 的全局配置。
    /// </summary>
    [Fact]
    public void ClaudeGetsPerLaunchExecFormSettingsWithoutGlobalConfigurationEdits()
    {
        var exe = Path.Combine(state, "my tools", "crewterm.exe");
        var launch = AgentHookLaunchService.Prepare(state, "crew-session", exe, "claude --session-id " + NativeId, "claude");
        Assert.True(launch.Enabled, launch.Reason);
        Assert.Contains(" --settings ", launch.Command);
        using var document = JsonDocument.Parse(File.ReadAllText(launch.SettingsPath!));
        var hooks = document.RootElement.GetProperty("hooks");
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "PermissionRequest", "Stop", "StopFailure", "Notification", "SessionEnd" })
        {
            var command = hooks.GetProperty(name)[0].GetProperty("hooks")[0];
            Assert.Equal(exe, command.GetProperty("command").GetString());
            var args = command.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("agent-hook", args);
            Assert.Contains("crew-session", args);
            Assert.Equal(3, command.GetProperty("timeout").GetInt32());
        }
    }

    /// <summary>
    /// 验证：在没有用户配置的 Codex 配置目录下，带 <c>--profile</c> 与含引号 <c>-c</c> 覆盖项的 Codex 命令被接入时，
    /// 原命令完整保留在开头，只追加调用 <c>agent-hook</c> 的 <c>notify=[...]</c> 覆盖，且不会添加任何 <c>dangerously</c> 类绕过信任的选项。
    /// </summary>
    [Fact]
    public void CodexUsesNotifyWithoutReplacingExistingLaunchOptionsOrHookTrust()
    {
        const string command = "codex --profile work -c \"model_provider='custom backend'\"";
        var launch = AgentHookLaunchService.Prepare(state, "crew-session", Path.Combine(state, "crewterm.exe"), command, "codex", codexHome: Path.Combine(state, "empty-home"));
        Assert.True(launch.Enabled, launch.Reason);
        Assert.StartsWith(command, launch.Command);
        Assert.Contains("notify=[", launch.Command);
        Assert.Contains("agent-hook", launch.Command);
        Assert.DoesNotContain("dangerously", launch.Command);
    }

    /// <summary>
    /// 验证：用户已显式配置相应机制（Claude 的 <c>--settings</c>、<c>--bare</c>、<c>--safe-mode</c>，Codex 命令行中的 <c>notify</c>），
    /// 只是查询信息的调用（<c>--version</c>、<c>--help</c>），或无法可靠解析的包装命令时，不接入回调：
    /// 命令原样返回，附带非空的原因说明，也不生成设置文件。
    /// </summary>
    /// <param name="command">原始启动命令。</param>
    /// <param name="provider">Provider 标识。</param>
    [Theory]
    [InlineData("claude --settings custom.json", "claude")]
    [InlineData("claude --bare", "claude")]
    [InlineData("claude --safe-mode", "claude")]
    [InlineData("claude --version", "claude")]
    [InlineData("codex -c \"notify=['my-existing-tool']\"", "codex")]
    [InlineData("codex --config=\"notify=['my-existing-tool']\"", "codex")]
    [InlineData("codex --help", "codex")]
    [InlineData("cmd.exe /c launch-agent.cmd", "codex")]
    public void UnsupportedOrUserConfiguredInvocationsRemainIntact(string command, string provider)
    {
        // 指定一个空的 Codex 配置目录，结果不受本机真实 CODEX_HOME / %USERPROFILE%\.codex 的影响。
        var launch = AgentHookLaunchService.Prepare(state, "crew-session", Path.Combine(state, "crewterm.exe"), command, provider,
            codexHome: Path.Combine(state, "empty-home"));
        Assert.False(launch.Enabled);
        Assert.Equal(command, launch.Command);
        Assert.NotEmpty(launch.Reason);
        Assert.Null(launch.SettingsPath);
    }

    /// <summary>
    /// 验证：Codex 用户级 <c>config.toml</c> 或所选 profile 的 <c>work.config.toml</c> 中已配置 <c>notify</c> 时不接入回调：
    /// 命令原样返回，原因中提及 <c>notify</c>，配置文件内容（含注释）保持不变。
    /// </summary>
    /// <param name="profile"><see langword="true"/> 时把配置写入 <c>work.config.toml</c> 并以 <c>--profile work</c> 启动，否则写入 <c>config.toml</c>。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexExistingUserAndSelectedProfileNotifyArePreserved(bool profile)
    {
        var home = Path.Combine(state, "codex-home");
        var configuration = Path.Combine(home, profile ? "work.config.toml" : "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configuration)!);
        const string original = "# Keep my notification program\nnotify = ['my-custom-notifier']\n";
        File.WriteAllText(configuration, original);
        var command = profile ? "codex --profile work" : "codex";
        var launch = AgentHookLaunchService.Prepare(state, "crew-session", Path.Combine(state, "crewterm.exe"), command, "codex", codexHome: home);
        Assert.False(launch.Enabled);
        Assert.Equal(command, launch.Command);
        Assert.Contains("notify", launch.Reason);
        Assert.Equal(original, File.ReadAllText(configuration));
    }

    /// <summary>
    /// 验证：项目目录 <c>.codex/config.toml</c> 中的 <c>notify</c>（Codex 本身会忽略项目级 notify）不会阻止接入本机回调，启动仍被接入。
    /// </summary>
    [Fact]
    public void ProjectNotifyIgnoredByProviderDoesNotSuppressMachineLocalHook()
    {
        var home = Path.Combine(state, "codex-home");
        var work = Path.Combine(state, "repo");
        Directory.CreateDirectory(Path.Combine(work, ".codex"));
        File.WriteAllText(Path.Combine(work, ".codex", "config.toml"), "notify=['ignored-project-command']");
        var launch = AgentHookLaunchService.Prepare(state, "crew-session", Path.Combine(state, "crewterm.exe"), "codex", "codex", codexHome: home);
        Assert.True(launch.Enabled, launch.Reason);
    }

    /// <summary>
    /// 若状态目录已被创建，则递归删除它及其中所有文件。
    /// </summary>
    public void Dispose() { if (Directory.Exists(state)) Directory.Delete(state, true); }
}
