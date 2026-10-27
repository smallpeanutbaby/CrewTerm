using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.App.Tests.Fixtures;

namespace CrewTerm.App.Tests.Server;

/// <summary>
/// 保存会话（原生会话目录）的 daemon 集成测试：在测试进程内运行真实的 <see cref="DaemonHost"/>，通过 <see cref="DaemonClient"/> 发送请求，
/// 覆盖关闭后保留休眠记录与遗忘、按原生会话标识恢复（含 daemon 重启后）、子进程身份环境变量、旧版快照 <c>saved-sessions.json</c> 与权威的原生目录
/// <c>native-sessions.json</c> 之间的关系、daemon 独占锁，以及带 Shell 元字符参数的恢复命令。
/// </summary>
public sealed class DaemonNativeSessionTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermNativeProtocolTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermNativeProtocolTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于停止当前 daemon 服务循环的取消源；<see cref="RestartAsync"/> 会替换为新实例。
    /// </summary>
    private CancellationTokenSource stop = new();

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
        host = new DaemonHost(state);
        server = host.RunAsync(stop.Token);
        Assert.True((await Client.CallAsync(new("ping"))).Ok);
    }

    /// <summary>
    /// 验证：关闭会话后它从存活列表中消失，但 <c>session-catalog</c> 中保留一条同标识的休眠记录；由于没有原生会话标识，<c>CanResume</c> 为 false 并给出原因；
    /// <c>session-forget</c> 后目录清空。
    /// </summary>
    [Fact]
    public async Task CloseKeepsDormantMetadataAndForgetRemovesItsRecord()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state, Name: "Kept shell"));
        Assert.True(created.Ok, created.Error);
        Assert.True((await Client.CallAsync(new("close", created.SessionId))).Ok);
        Assert.Empty((await Client.CallAsync(new("list"))).Sessions!);
        var catalog = await Client.CallAsync(new("session-catalog"));
        Assert.True(catalog.Ok, catalog.Error);
        var dormant = Assert.Single(catalog.Sessions!);
        Assert.Equal(created.SessionId, dormant.Id);
        Assert.True(dormant.IsDormant);
        Assert.False(dormant.CanResume);
        Assert.NotEmpty(dormant.ResumeReason!);
        Assert.True((await Client.CallAsync(new("session-forget", created.SessionId))).Ok);
        Assert.Empty((await Client.CallAsync(new("session-catalog"))).Sessions!);
    }

    /// <summary>
    /// 验证：提供方为 <c>codex</c> 但未绑定原生会话标识的会话关闭后，<c>session-resume</c> 失败并提示缺少 <c>Native conversation ID</c>，
    /// 而不是悄悄以全新会话重启（存活列表保持为空）。
    /// </summary>
    [Fact]
    public async Task MissingNativeIdentityDoesNotSilentlyRestartAsResume()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state, Provider: "codex"));
        Assert.True(created.Ok, created.Error);
        await Client.CallAsync(new("close", created.SessionId));
        var resumed = await Client.CallAsync(new("session-resume", created.SessionId));
        Assert.False(resumed.Ok);
        Assert.Contains("Native conversation ID", resumed.Error);
        Assert.Empty((await Client.CallAsync(new("list"))).Sessions!);
    }

    /// <summary>
    /// 验证：为伪造的 <c>codex</c> 会话绑定原生标识并重启 daemon 后，该会话在目录中为可恢复的休眠记录且保留原生标识；新建另一个会话不会丢失这条记录（目录共 2 条）；
    /// <c>session-resume</c> 复用原会话标识，以 <c>resume &lt;原生标识&gt;</c> 调用提供方命令（脚本把收到的标识写入文件以便核对），恢复后的会话出现在存活列表中。
    /// </summary>
    [Fact]
    public async Task RestartedDaemonResumesExactOriginalNativeIdWithoutLosingOtherRecords()
    {
        var fixture = TestShell.WriteResumeRecorder(Directory.CreateDirectory(Path.Combine(state, "fixture tools")).FullName, "codex", "resumed-id.txt");
        var identityFile = Path.Combine(state, "resumed-id.txt");
        var created = await Client.CallAsync(new("create", Command: "\"" + fixture + "\"", WorkingDirectory: state, Provider: "codex"));
        Assert.True(created.Ok, created.Error);
        const string nativeId = "871fcb41-2d23-48cb-9f86-e9d03934cfd1";
        var bound = await Client.CallAsync(new("session-bind", created.SessionId, Provider: "codex", NativeSessionId: nativeId));
        Assert.True(bound.Ok, bound.Error);
        await RestartAsync();
        var dormant = Assert.Single((await Client.CallAsync(new("session-catalog"))).Sessions!);
        Assert.True(dormant.IsDormant);
        Assert.True(dormant.CanResume);
        Assert.Equal(nativeId, dormant.NativeSessionId);
        var another = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.True(another.Ok, another.Error);
        Assert.Equal(2, (await Client.CallAsync(new("session-catalog"))).Sessions!.Length);
        var resumed = await Client.CallAsync(new("session-resume", created.SessionId));
        Assert.True(resumed.Ok, resumed.Error);
        Assert.Equal(created.SessionId, resumed.SessionId);
        Assert.Equal(nativeId, (await Poll.ReadAllTextAsync(identityFile)).Trim());
        Assert.Single((await Client.CallAsync(new("list"))).Sessions!, item => item.Id == created.SessionId);
    }

    /// <summary>
    /// 验证：对仍在运行的会话执行 <c>session-resume</c> 会成功返回同一会话标识，但不会重复启动（存活列表仍只有一条且启动时间不变）。
    /// </summary>
    [Fact]
    public async Task ResumeOfRunningSessionReturnsSameIdentityWithoutDuplicateLaunch()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        var original = Assert.Single((await Client.CallAsync(new("list"))).Sessions!);
        var resumed = await Client.CallAsync(new("session-resume", created.SessionId));
        Assert.True(resumed.Ok, resumed.Error);
        var current = Assert.Single((await Client.CallAsync(new("list"))).Sessions!);
        Assert.Equal(created.SessionId, resumed.SessionId);
        Assert.Equal(original.StartedAt, current.StartedAt);
    }

    /// <summary>
    /// 验证：会话子进程中的环境变量 <c>CREWTERM_SESSION_ID</c> 等于该会话的标识（由子进程写入文件后核对），而测试进程自身的同名环境变量保持不变。
    /// </summary>
    [Fact]
    public async Task ChildReceivesCrewTermIdentityWithoutChangingParentEnvironment()
    {
        var parentValue = Environment.GetEnvironmentVariable("CREWTERM_SESSION_ID");
        var created = await Client.CallAsync(new("create", Command: TestShell.WriteVariable("CREWTERM_SESSION_ID", "session-id.txt"), WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        var path = Path.Combine(state, "session-id.txt");
        Assert.Equal(created.SessionId, (await Poll.ReadAllTextAsync(path)).Trim());
        Assert.Equal(parentValue, Environment.GetEnvironmentVariable("CREWTERM_SESSION_ID"));
    }

    /// <summary>
    /// 验证：遗忘一个进程已退出的会话后重启 daemon，该会话不会因旧版快照中的元数据而重新出现在目录中。
    /// </summary>
    [Fact]
    public async Task ForgettingExitedSessionIsNotUndoneByLegacyMetadataOnRestart()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Exit(0), WorkingDirectory: state));
        Assert.True((await Client.CallAsync(new("agent-wait", created.SessionId,
            State: CrewTerm.Automation.Activity.AgentActivityState.Exited, TimeoutMilliseconds: 3000))).Ok);
        Assert.True((await Client.CallAsync(new("session-forget", created.SessionId))).Ok);
        await RestartAsync();
        Assert.Empty((await Client.CallAsync(new("session-catalog"))).Sessions!);
    }

    /// <summary>
    /// 验证：在 <c>native-sessions.json</c> 的位置预先建立同名目录使原生元数据写入失败时，<c>create</c> 失败且不留下存活会话；
    /// 删除该目录并重启 daemon 后，目录中也不会从旧版快照导入这次失败创建的“幽灵”记录。
    /// </summary>
    [Fact]
    public async Task FailedNativeMetadataWriteCannotBecomeLegacyGhostAfterRestart()
    {
        var nativePath = Directory.CreateDirectory(Path.Combine(state, "native-sessions.json")).FullName;
        var result = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        Assert.False(result.Ok);
        Assert.Empty((await Client.CallAsync(new("list"))).Sessions!);
        Directory.Delete(nativePath);
        await RestartAsync();
        Assert.Empty((await Client.CallAsync(new("session-catalog"))).Sessions!);
    }

    /// <summary>
    /// 验证：成功创建带提供方元数据的会话后，旧版快照 <c>saved-sessions.json</c> 中唯一的记录包含当前的提供方 <c>custom</c> 和原生会话标识 <c>original-42</c>。
    /// </summary>
    [Fact]
    public async Task SuccessfulLegacySnapshotContainsCurrentProviderMetadata()
    {
        var result = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state,
            Provider: "custom", NativeSessionId: "original-42", ResumeCommand: TestShell.Interactive));
        Assert.True(result.Ok, result.Error);
        var saved = Assert.Single(JsonSerializer.Deserialize<SessionInfo[]>(File.ReadAllText(Path.Combine(state, "saved-sessions.json")))!);
        Assert.Equal("custom", saved.Provider);
        Assert.Equal("original-42", saved.NativeSessionId);
    }

    /// <summary>
    /// 验证：以提供方 <c>claude</c> 启动的会话，旧版快照中保存的是用户原始命令，不包含为本次启动临时生成的 <c>provider-hooks</c> 钩子配置。
    /// </summary>
    [Fact]
    public async Task LegacySnapshotRetainsOriginalCommandWithoutTransientGeneratedHookSettings()
    {
        var fixture = TestShell.WriteIdleProgram(Directory.CreateDirectory(Path.Combine(state, "fixture tools")).FullName, "claude");
        var command = "\"" + fixture + "\"";
        var result = await Client.CallAsync(new("create", Command: command, WorkingDirectory: state, Provider: "claude"));
        Assert.True(result.Ok, result.Error);
        var saved = Assert.Single(JsonSerializer.Deserialize<SessionInfo[]>(File.ReadAllText(Path.Combine(state, "saved-sessions.json")))!);
        Assert.Equal(command, saved.Command);
        Assert.DoesNotContain("provider-hooks", saved.Command);
    }

    /// <summary>
    /// 验证：原生目录一旦存在（即使为空）就是权威来源：会话关闭并遗忘后，即使把它的旧记录重新写回 <c>saved-sessions.json</c> 并重启 daemon，目录仍为空。
    /// </summary>
    [Fact]
    public async Task AuthoritativeNativeCatalogDoesNotReimportStaleLegacyRecords()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        var original = Assert.Single((await Client.CallAsync(new("list"))).Sessions!);
        await Client.CallAsync(new("close", created.SessionId));
        await Client.CallAsync(new("session-forget", created.SessionId));
        File.WriteAllText(Path.Combine(state, "saved-sessions.json"), JsonSerializer.Serialize(new[] { original }));
        await RestartAsync();
        Assert.Empty((await Client.CallAsync(new("session-catalog"))).Sessions!);
    }

    /// <summary>
    /// 验证：同一状态目录上的第二个 <see cref="DaemonHost"/> 因拿不到 daemon 独占锁而在 <see cref="DaemonHost.RunAsync"/> 中抛出 <see cref="IOException"/>，
    /// 并且不会在获取锁之前改写已持久化的 Agent 状态（仍为 <c>Working</c>）。
    /// </summary>
    [Fact]
    public async Task AnotherHostCannotRewriteLiveActivityBeforeAcquiringDaemonLock()
    {
        var created = await Client.CallAsync(new("create", Command: TestShell.Interactive, WorkingDirectory: state));
        await Client.CallAsync(new("agent-report", created.SessionId, State: CrewTerm.Automation.Activity.AgentActivityState.Working, Reason: "Active work"));
        using var competing = new DaemonHost(state);
        await Assert.ThrowsAsync<IOException>(() => competing.RunAsync());
        var persisted = new CrewTerm.Automation.Activity.AgentActivityStore(state).Get(created.SessionId!);
        Assert.Equal(CrewTerm.Automation.Activity.AgentActivityState.Working, persisted.State);
    }

    /// <summary>
    /// 验证：<c>codex</c> 会话的启动命令带有 <c>--profile</c>、含空格的 <c>-c</c> 配置值和含 <c>&amp;</c> 的 <c>--cd</c> 目录时，daemon 重启后 <c>session-resume</c>
    /// 经伪终端启动的恢复命令把这些参数原样传给提供方（由 PowerShell 脚本把收到的参数记录为 JSON），并附加 <c>resume</c> 与原生会话标识。
    /// </summary>
    [Fact]
    public async Task TerminalResumePreservesProfileConfigAndDirectoryWithShellMetacharacters()
    {
        var project = Directory.CreateDirectory(Path.Combine(state, "project & folder")).FullName;
        var argsFile = Path.Combine(state, "resumed-args.json");
        var script = Path.Combine(state, "record args.ps1");
        PowerShellFixture.WriteScript(script, "if ($args -contains 'resume') { [IO.File]::WriteAllText('" + argsFile.Replace("'", "''")
            + "', (ConvertTo-Json -InputObject @($args) -Compress)); exit 0 }; Start-Sleep -Seconds 60");
        var fixture = TestShell.WriteLauncher(Directory.CreateDirectory(Path.Combine(state, "fixture tools")).FullName, "codex", PowerShellFixture.CommandLine(script));
        var command = "\"" + fixture + "\" --profile work -c \"model_provider=custom backend\" --cd \"" + project + "\"";
        var created = await Client.CallAsync(new("create", Command: command, WorkingDirectory: state, Provider: "codex"));
        Assert.True(created.Ok, created.Error);
        const string nativeId = "871fcb41-2d23-48cb-9f86-e9d03934cfd1";
        Assert.True((await Client.CallAsync(new("session-bind", created.SessionId, Provider: "codex", NativeSessionId: nativeId))).Ok);
        await RestartAsync();
        var resumed = await Client.CallAsync(new("session-resume", created.SessionId));
        Assert.True(resumed.Ok, resumed.Error);
        var arguments = JsonSerializer.Deserialize<string[]>(await Poll.ReadAllTextAsync(argsFile))!;
        Assert.Contains("work", arguments);
        Assert.Contains("model_provider=custom backend", arguments);
        Assert.Contains(project, arguments);
        Assert.Contains("resume", arguments);
        Assert.Contains(nativeId, arguments);
    }

    /// <summary>
    /// 模拟 daemon 重启：取消并等待当前服务循环结束，释放宿主与取消源，再用同一状态目录重新创建并启动 daemon。
    /// </summary>
    /// <returns>表示重启过程的任务。</returns>
    private async Task RestartAsync()
    {
        await stop.CancelAsync(); await server!; host!.Dispose(); stop.Dispose(); stop = new();
        await InitializeAsync();
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
