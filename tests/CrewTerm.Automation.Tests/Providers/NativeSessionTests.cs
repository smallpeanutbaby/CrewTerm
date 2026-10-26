using CrewTerm.Automation.Providers;
using CrewTerm.Automation.Tasks;
using CrewTerm.Automation.Tests.Fixtures;

namespace CrewTerm.Automation.Tests.Providers;

/// <summary>
/// 覆盖 <see cref="ProviderSessionService"/> 为各 Provider 生成启动、恢复与重新启动命令的规则（只使用精确的原生对话标识，
/// 保留已验证的配置选项、丢弃初始提示词，遇到无法确定的命令时要求显式登记），以及 <see cref="NativeSessionStore"/> 的持久化。
/// 多数测试是纯逻辑单元测试；存储测试在系统临时目录中读写真实文件，另有两个测试通过 <see cref="TaskRunner"/>
/// 运行真实的 <c>.cmd</c> 替身程序，验证恢复命令实际传给进程的参数。
/// </summary>
public sealed class NativeSessionTests : IDisposable
{
    /// <summary>
    /// 测试中使用的精确原生对话标识（GUID 格式）。
    /// </summary>
    private const string Conversation = "550e8400-e29b-41d4-a716-446655440000";
    /// <summary>
    /// 本测试实例专用的临时根目录（以随机 GUID 命名），既用作会话存储的状态目录，也用作会话的工作目录。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermNative", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public NativeSessionTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：启动 Claude 时预先分配一个 GUID 格式的原生对话标识，在命令末尾追加 <c>--session-id</c>；
    /// 对应的恢复命令为 <c>claude --resume &lt;标识&gt;</c> 并保留 <c>--model sonnet</c>，计划标记为可恢复。
    /// </summary>
    [Fact]
    public void ClaudeLaunchPreallocatesAnExactNativeIdentity()
    {
        var plan = new ProviderSessionService().PrepareLaunch("claude --model sonnet");
        Assert.Equal("claude", plan.Provider);
        Assert.True(Guid.TryParseExact(plan.NativeSessionId, "D", out _));
        Assert.Equal("claude --model sonnet --session-id " + plan.NativeSessionId, plan.Command);
        Assert.Equal("claude --resume " + plan.NativeSessionId + " --model sonnet", plan.ResumeCommand);
        Assert.True(plan.CanResume);
    }

    /// <summary>
    /// 验证：Claude、Codex、Gemini、Copilot 各自用其恢复语法只恢复已绑定的那个对话标识，计划可恢复，
    /// 且命令中绝不出现 <c>--last</c> 这类“最近会话”选择器。
    /// </summary>
    /// <param name="provider">Provider 标识（同时用作启动命令）。</param>
    /// <param name="expected">期望生成的恢复命令。</param>
    [Theory]
    [InlineData("claude", "claude --resume 550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("codex", "codex resume 550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("gemini", "gemini --resume 550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("copilot", "copilot --resume=550e8400-e29b-41d4-a716-446655440000")]
    public void KnownProvidersResumeOnlyTheBoundConversation(string provider, string expected)
    {
        var saved = new NativeSessionRecord("crew-id", "Agent", provider, root, provider, Conversation);
        var plan = new ProviderSessionService().PrepareResume(saved);
        Assert.Equal(expected, plan.Command);
        Assert.Equal(Conversation, plan.NativeSessionId);
        Assert.True(plan.CanResume);
        Assert.DoesNotContain("--last", plan.Command);
    }

    /// <summary>
    /// 验证：OpenCode 使用其显式的 <c>--session</c> 参数恢复，接受 <c>ses_</c> 前缀格式的会话标识。
    /// </summary>
    [Fact]
    public void OpenCodeUsesItsExplicitSessionArgument()
    {
        var saved = new NativeSessionRecord("crew-id", "Agent", "opencode", root, "opencode", "ses_exact123");
        Assert.Equal("opencode --session ses_exact123", new ProviderSessionService().PrepareResume(saved).Command);
    }

    /// <summary>
    /// 验证：<c>--help</c>、<c>--version</c> 这类工具性命令原样启动，不会被分配虚构的对话标识，也不可恢复。
    /// </summary>
    /// <param name="command">工具性启动命令。</param>
    [Theory]
    [InlineData("claude --help")]
    [InlineData("claude --version")]
    [InlineData("codex resume --help")]
    [InlineData("codex --version")]
    public void UtilityCommandsAreNeverAssignedFictionalConversationIds(string command)
    {
        var plan = new ProviderSessionService().PrepareLaunch(command);
        Assert.Equal(command, plan.Command);
        Assert.Null(plan.NativeSessionId);
        Assert.False(plan.CanResume);
    }

    /// <summary>
    /// 验证：启动命令本身已精确选定对话（<c>--resume</c>、<c>--session-id</c> 或 <c>codex resume</c> 加标识）时，
    /// 命令原样保留、不再追加另一个选择器，并从中捕获该标识，计划可恢复。
    /// </summary>
    /// <param name="command">已带精确对话选择的启动命令。</param>
    [Theory]
    [InlineData("claude --resume 550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("claude --session-id 550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("codex resume 550e8400-e29b-41d4-a716-446655440000")]
    public void ExistingExactSelectionsAreCapturedWithoutAddingAnotherSelection(string command)
    {
        var plan = new ProviderSessionService().PrepareLaunch(command);
        Assert.Equal(command, plan.Command);
        Assert.Equal(Conversation, plan.NativeSessionId);
        Assert.True(plan.CanResume);
    }

    /// <summary>
    /// 验证：启动时无法得知标识（<c>codex</c>）或只用模糊选择器（<c>resume --last</c>、<c>--continue</c>）的命令不产生标识、不可恢复；
    /// 据此保存的记录同样报告不可恢复，<see cref="ProviderSessionService.PrepareResume"/> 抛出 <see cref="InvalidOperationException"/>，
    /// 必须先显式绑定标识。
    /// </summary>
    /// <param name="command">缺少精确标识或标识含糊的启动命令。</param>
    [Theory]
    [InlineData("codex")]
    [InlineData("codex resume --last")]
    [InlineData("claude --continue")]
    public void MissingOrAmbiguousIdentityRequiresExplicitBinding(string command)
    {
        var service = new ProviderSessionService();
        var launch = service.PrepareLaunch(command);
        Assert.False(launch.CanResume);
        Assert.Null(launch.NativeSessionId);
        var saved = new NativeSessionRecord("crew-id", "Agent", command, root, launch.Provider);
        Assert.False(service.GetResumeCapability(saved).CanResume);
        Assert.Throws<InvalidOperationException>(() => service.PrepareResume(saved));
    }

    /// <summary>
    /// 验证：内置 Provider（此处为 Codex）的记录中，若原生标识是 <c>--last</c> 选择器、会话名称或夹带 Shell 片段的文本，
    /// 生成恢复命令时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="id">不合法的原生对话标识。</param>
    [Theory]
    [InlineData("--last")]
    [InlineData("shared-session-name")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000 & echo injected")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000\n")]
    [InlineData(" 550e8400-e29b-41d4-a716-446655440000")]
    public void BuiltInProvidersRejectNamesAndShellFragmentsAsNativeIds(string id)
    {
        var saved = new NativeSessionRecord("crew-id", "Agent", "codex", root, "codex", id);
        Assert.Throws<ArgumentException>(() => new ProviderSessionService().PrepareResume(saved));
    }

    /// <summary>
    /// 验证：OpenCode 这类按通用规则校验的标识，末尾夹带换行（正则 <c>$</c> 会放过的情况）同样被拒绝。
    /// </summary>
    [Fact]
    public void GenericNativeIdsCannotEndWithALineBreak()
    {
        var saved = new NativeSessionRecord("crew-id", "Agent", "opencode", root, "opencode", "ses_abc\n");
        Assert.Throws<ArgumentException>(() => new ProviderSessionService().PrepareResume(saved));
    }

    /// <summary>
    /// 验证：<c>codex resume -- 文本</c> 中 <c>--</c> 之后的内容是提示词，不会被当作恢复选中的原生会话标识。
    /// </summary>
    [Fact]
    public void TextAfterEndOfOptionsIsNotTakenAsTheResumeSelection()
    {
        var plan = new ProviderSessionService().PrepareLaunch("codex resume -- 550e8400-e29b-41d4-a716-446655440000");
        Assert.Null(plan.NativeSessionId);
        Assert.False(plan.CanResume);
    }

    /// <summary>
    /// 验证：自定义 Provider 显式登记了恢复命令时，恢复使用该命令；而普通的重新启动使用原始启动命令，
    /// 不携带对话标识，也不可恢复。
    /// </summary>
    [Fact]
    public void ExplicitCustomResumeAndOrdinaryRestartHaveDifferentCommands()
    {
        var saved = new NativeSessionRecord("crew-id", "Custom", "custom-cli --new", root, "custom", "thread-17", "custom-cli --resume thread-17");
        var service = new ProviderSessionService();
        Assert.Equal("custom-cli --resume thread-17", service.PrepareResume(saved).Command);
        var restarted = service.PrepareRestart(saved);
        Assert.Equal("custom-cli --new", restarted.Command);
        Assert.Null(restarted.NativeSessionId);
        Assert.False(restarted.CanResume);
    }

    /// <summary>
    /// 验证：重新启动 Claude 会分配一个不同于原对话的新标识，命令中不包含 <c>--resume</c>。
    /// </summary>
    [Fact]
    public void RestartingClaudeAllocatesAnotherConversation()
    {
        var saved = new NativeSessionRecord("crew-id", "Claude", "claude", root, "claude", Conversation);
        var restarted = new ProviderSessionService().PrepareRestart(saved);
        Assert.NotEqual(Conversation, restarted.NativeSessionId);
        Assert.DoesNotContain("--resume", restarted.Command);
    }

    /// <summary>
    /// 验证：不持久化会话（<c>--no-session-persistence</c>）或从原对话分叉（<c>--fork-session</c>、<c>--fork</c>）的启动命令原样保留，
    /// 不会把原对话标识当作本会话的标识，也不可恢复。
    /// </summary>
    /// <param name="command">分叉或不持久化的启动命令。</param>
    [Theory]
    [InlineData("claude --no-session-persistence --print")]
    [InlineData("claude --resume 550e8400-e29b-41d4-a716-446655440000 --fork-session")]
    [InlineData("opencode --session ses_original --fork")]
    public void ForkedOrNonPersistentLaunchesDoNotClaimTheOriginalConversation(string command)
    {
        var plan = new ProviderSessionService().PrepareLaunch(command);
        Assert.Equal(command, plan.Command);
        Assert.Null(plan.NativeSessionId);
        Assert.False(plan.CanResume);
    }

    /// <summary>
    /// 验证：同一状态目录上的两个独立存储实例分别写入旧会话与新会话后，重新打开的存储保留全部 2 条记录，
    /// 旧会话的对话标识与新会话的启动命令都没有丢失。
    /// </summary>
    [Fact]
    public void NewSessionsAndIndependentStoreInstancesPreserveDormantRecords()
    {
        var first = new NativeSessionStore(root);
        var second = new NativeSessionStore(root);
        first.Upsert(new("old", "Old", "codex", root, "codex", Conversation));
        second.Upsert(new("new", "New", "cmd.exe", root));

        var recovered = new NativeSessionStore(root);
        Assert.Equal(2, recovered.List().Count);
        Assert.Equal(Conversation, recovered.Get("old")!.NativeSessionId);
        Assert.Equal("cmd.exe", recovered.Get("new")!.Command);
    }

    /// <summary>
    /// 验证：显式绑定的对话标识会被持久化；忘记会话第一次返回 <see langword="true"/>、第二次返回 <see langword="false"/>，
    /// 重新打开后只剩未被忘记的 <c>kept</c> 记录。
    /// </summary>
    [Fact]
    public void BindingAndForgettingAreExplicitDurableOperations()
    {
        var store = new NativeSessionStore(root);
        store.Upsert(new("kept", "Agent", "codex", root));
        store.Upsert(new("other", "Other", "cmd.exe", root));
        store.Bind("kept", "codex", Conversation);
        Assert.Equal(Conversation, new NativeSessionStore(root).Get("kept")!.NativeSessionId);
        Assert.True(store.Forget("other"));
        Assert.False(store.Forget("other"));
        Assert.Equal("kept", Assert.Single(new NativeSessionStore(root).List()).Id);
    }

    /// <summary>
    /// 验证：以带引号的 <c>codex.cmd</c> 替身路径保存的 Codex 会话，重新打开存储后生成的恢复命令经 <see cref="TaskRunner"/>
    /// 真实运行，替身程序收到的参数恰为 <c>resume &lt;标识&gt;</c>。
    /// </summary>
    [Fact]
    public async Task ReopenedCatalogResumesExactIdThroughARealLocalFixture()
    {
        var fixture = Path.Combine(root, "codex.cmd");
        File.WriteAllText(fixture, "@echo off\r\necho %* > received.txt\r\n");
        new NativeSessionStore(root).Upsert(new("fixture", "Fixture", $"\"{fixture}\"", root, "codex", Conversation));
        var recovered = new NativeSessionStore(root).Get("fixture")!;
        var plan = new ProviderSessionService().PrepareResume(recovered);

        var execution = await new TaskRunner().RunAsync(new("native fixture", plan.Command, recovered.WorkingDirectory));

        Assert.True(execution.Succeeded, execution.Output);
        Assert.Equal("resume " + Conversation, File.ReadAllText(Path.Combine(root, "received.txt")).Trim());
    }

    /// <summary>
    /// 验证：恢复命令改用 Provider 的恢复语法，保留已验证的配置选项（如 <c>--profile</c>、<c>-c</c>、<c>--cd</c>、<c>--model</c>、
    /// <c>--settings</c>、<c>--permission-mode</c>、<c>--system-prompt</c>，含带引号和空格的取值），去掉初始提示词，
    /// 并移除原命令中已有的对话选择器（<c>--session-id</c>、无参数的 <c>--resume</c>）。
    /// </summary>
    /// <param name="command">原始启动命令。</param>
    /// <param name="expected">期望生成的恢复命令。</param>
    [Theory]
    [InlineData("codex --profile work -c model_provider=local --cd \"C:\\work space\" \"initial request\"", "codex resume 550e8400-e29b-41d4-a716-446655440000 --profile work -c model_provider=local --cd \"C:\\work space\"")]
    [InlineData("codex --profile=work --model chosen --no-alt-screen", "codex resume 550e8400-e29b-41d4-a716-446655440000 --profile=work --model chosen --no-alt-screen")]
    [InlineData("claude --settings \"C:\\settings folder\\work.json\" --model sonnet \"initial request\"", "claude --resume 550e8400-e29b-41d4-a716-446655440000 --settings \"C:\\settings folder\\work.json\" --model sonnet")]
    [InlineData("claude --settings=\"C:\\settings folder\\work.json\" --permission-mode plan --system-prompt \"keep these instructions\"", "claude --resume 550e8400-e29b-41d4-a716-446655440000 --settings=\"C:\\settings folder\\work.json\" --permission-mode plan --system-prompt \"keep these instructions\"")]
    [InlineData("claude --model sonnet --session-id 550e8400-e29b-41d4-a716-446655440000 --resume", "claude --resume 550e8400-e29b-41d4-a716-446655440000 --model sonnet")]
    public void ResumePreservesVerifiedConfigurationOptionsAndOmitsTheInitialPrompt(string command, string expected)
    {
        var service = new ProviderSessionService();
        var saved = new NativeSessionRecord("crew", "Agent", command, root, NativeSessionId: Conversation);
        var resume = service.PrepareResume(saved);
        Assert.Equal(expected, resume.Command);
    }

    /// <summary>
    /// 验证：原命令为 <c>codex --profile work resume &lt;标识&gt; "old prompt"</c> 时，恢复命令去掉旧的 <c>resume</c> 选择器和提示词，
    /// 生成 <c>codex resume &lt;标识&gt; --profile work</c>，保留 profile。
    /// </summary>
    [Fact]
    public void ResumeRemovesThePreviousSelectorButKeepsItsProfile()
    {
        var saved = new NativeSessionRecord("crew", "Agent", "codex --profile work resume " + Conversation + " \"old prompt\"", root, "codex", Conversation);
        Assert.Equal("codex resume " + Conversation + " --profile work", new ProviderSessionService().PrepareResume(saved).Command);
    }

    /// <summary>
    /// 验证：含未知选项、缺少取值的选项、取值个数不定的选项（<c>--image</c>）、<c>&amp;&amp;</c> 复合命令或 <c>cmd.exe</c> 包装的命令无法可靠改写，
    /// 因此报告不可恢复、<see cref="ProviderSessionService.PrepareResume"/> 抛出 <see cref="InvalidOperationException"/>；
    /// 显式登记恢复命令后则直接使用该命令。
    /// </summary>
    /// <param name="command">无法可靠改写为恢复命令的启动命令。</param>
    [Theory]
    [InlineData("codex --future-option work")]
    [InlineData("codex --profile")]
    [InlineData("codex --image a.png b.png \"initial request\"")]
    [InlineData("claude --resume 550e8400-e29b-41d4-a716-446655440000 && claude")]
    [InlineData("cmd.exe /c codex")]
    public void AmbiguousResumeCommandsRequireAnExplicitResumeRegistration(string command)
    {
        var service = new ProviderSessionService();
        var saved = new NativeSessionRecord("crew", "Agent", command, root, "codex", Conversation);
        Assert.False(service.GetResumeCapability(saved).CanResume);
        Assert.Throws<InvalidOperationException>(() => service.PrepareResume(saved));
        Assert.Equal("custom-wrapper resume exact", service.PrepareResume(saved with { ResumeCommand = "custom-wrapper resume exact" }).Command);
    }

    /// <summary>
    /// 验证：Claude 命令用 <c>--</c> 结束选项后跟提示词时，预分配的 <c>--session-id</c> 插入在 <c>--</c> 之前，
    /// 不会被当作提示词的一部分；恢复命令只保留 <c>--model sonnet</c>。
    /// </summary>
    [Fact]
    public void ClaudeEndOfOptionsPromptCannotSwallowThePreallocatedSessionFlag()
    {
        var service = new ProviderSessionService();
        var launch = service.PrepareLaunch("claude --model sonnet -- \"initial request\"");
        Assert.Equal("claude --model sonnet --session-id " + launch.NativeSessionId + " -- \"initial request\"", launch.Command);
        Assert.Equal("claude --resume " + launch.NativeSessionId + " --model sonnet", launch.ResumeCommand);
    }

    /// <summary>
    /// 验证：用 <c>&amp;&amp;</c> 连接的复合启动命令即使其中一条带精确标识，也不会据此认定整个会话的标识，计划不可恢复。
    /// </summary>
    [Fact]
    public void CompoundLaunchDoesNotClaimAnExactIdFromOnlyOneOfItsCommands()
    {
        var launch = new ProviderSessionService().PrepareLaunch("claude --resume " + Conversation + " && claude");
        Assert.Null(launch.NativeSessionId);
        Assert.False(launch.CanResume);
    }

    /// <summary>
    /// 验证：重新启动 <c>codex --profile work resume &lt;标识&gt;</c> 时去掉旧的对话选择，只保留配置得到 <c>codex --profile work</c>，且不携带标识。
    /// </summary>
    [Fact]
    public void RestartRemovesTheOldSelectionWhilePreservingConfiguration()
    {
        var saved = new NativeSessionRecord("crew", "Agent", "codex --profile work resume " + Conversation, root, "codex", Conversation);
        var restarted = new ProviderSessionService().PrepareRestart(saved);
        Assert.Equal("codex --profile work", restarted.Command);
        Assert.Null(restarted.NativeSessionId);
    }

    /// <summary>
    /// 验证：<c>codex.cmd</c> 替身把参数转交给 PowerShell 脚本，脚本以 JSON 记录收到的参数；重新打开存储后生成的恢复命令经
    /// <see cref="TaskRunner"/> 真实运行，进程收到的参数依次为 <c>resume</c>、标识、<c>--profile work</c>、带空格的 <c>-c</c> 取值
    /// 以及名称含 <c>&amp;</c> 的 <c>--cd</c> 目录，均保持字面值，且不含旧提示词。
    /// </summary>
    [Fact]
    public async Task ReopenedCatalogRetainsLiteralConfigurationArgumentsThroughARealProcess()
    {
        var script = Path.Combine(root, "capture.ps1");
        PowerShellFixture.WriteScript(script, "[IO.File]::WriteAllText('arguments.json', (ConvertTo-Json -InputObject @($args) -Compress))");
        var fixture = Path.Combine(root, "codex.cmd");
        File.WriteAllText(fixture, $"@{PowerShellFixture.CommandLine(script)} %*\r\n");
        var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "project & folder")).FullName;
        var original = $"\"{fixture}\" --profile work -c \"model_provider=custom backend\" --cd \"{workingDirectory}\" \"old prompt\"";
        new NativeSessionStore(root).Upsert(new("fixture", "Fixture", original, root, "codex", Conversation));
        var recovered = new NativeSessionStore(root).Get("fixture")!;
        var plan = new ProviderSessionService().PrepareResume(recovered);

        var execution = await new TaskRunner().RunAsync(new("native arguments", plan.Command, root));

        Assert.True(execution.Succeeded, execution.Output);
        var arguments = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "arguments.json")));
        Assert.Equal(new[] { "resume", Conversation, "--profile", "work", "-c", "model_provider=custom backend", "--cd", workingDirectory }, arguments);
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, true);
}
