using System.Text.RegularExpressions;

namespace CrewTerm.Automation.Providers;

/// <summary>
/// 准备经过验证的原生 CLI 启动与恢复命令，绝不退回到“最近一次会话”之类的选择器。
/// </summary>
/// <remarks>
/// <para>支持的 Provider：<c>claude</c>、<c>codex</c>、<c>gemini</c>、<c>opencode</c>、<c>copilot</c>。
/// 恢复只使用记录中的精确原生对话标识或用户显式登记的恢复命令；身份缺失或含糊时拒绝恢复并说明原因，
/// 而不是猜测另一个对话。“重新启动”始终开启新对话，与恢复是两个独立的操作。</para>
/// <para>本类无状态，可在多个线程间共享。</para>
/// </remarks>
public sealed class ProviderSessionService
{
    /// <summary>
    /// 有原生恢复适配器的 Provider 标识（小写）。
    /// </summary>
    private static readonly HashSet<string> Known = ["claude", "codex", "gemini", "opencode", "copilot"];

    /// <summary>
    /// 作为第一个参数出现时表示工具类调用（帮助、登录、MCP 管理、更新等）的子命令；这类调用不会开启对话。
    /// </summary>
    private static readonly HashSet<string> Utilities = ["help", "login", "logout", "auth", "mcp", "doctor", "update", "install", "completion", "plugin", "agents"];

    /// <summary>
    /// 准备一次启动：为 Claude 新对话预先分配会话标识，并捕获命令中精确的对话选择器。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>帮助、版本或工具类子命令：原样启动，不可恢复。</description></item>
    /// <item><description>含 <c>--no-session-persistence</c>、<c>--fork-session</c> 或 <c>--fork</c>：原样启动，
    /// 需等真正的新标识出现后再显式绑定。</description></item>
    /// <item><description>已知 Provider 的命令无法被受限语法解析：原样启动；只有提供了显式恢复命令时才可恢复。</description></item>
    /// <item><description>未知 Provider：原样启动；只有提供了显式恢复命令时才可恢复。</description></item>
    /// <item><description>命令选中了精确标识：采用该标识；选中的不是精确标识或是“最近会话”选择器时不可恢复（除非有显式恢复命令）。</description></item>
    /// <item><description>Claude 新对话：追加 <c>--session-id &lt;新 GUID&gt;</c>（或调用方给出的标识），插在 <c>--</c> 之前。</description></item>
    /// <item><description>其他已知 Provider 且调用方给出了原生标识：把命令改写为恢复该标识的命令并保留原有选项。</description></item>
    /// </list>
    /// </remarks>
    /// <param name="command">原始的本地 CLI 命令。</param>
    /// <param name="provider">显式指定的 Provider 标识（会去空白并转小写）；为 <see langword="null"/> 时根据可执行文件名推断。</param>
    /// <param name="nativeSessionId">显式指定的原生对话标识；对已知 Provider，启动命令会选中这个标识。</param>
    /// <param name="resumeCommand">需要保留的显式自定义恢复命令。</param>
    /// <returns>实际要执行的命令、Provider、原生标识以及能否恢复的说明。</returns>
    /// <exception cref="ArgumentException">命令为空白或含 NUL；<paramref name="nativeSessionId"/> 不符合该 Provider 的标识格式；
    /// 或命令中选中的标识与 <paramref name="nativeSessionId"/> 不一致。</exception>
    public ProviderLaunchPlan PrepareLaunch(string command, string? provider = null, string? nativeSessionId = null, string? resumeCommand = null)
    {
        var lexed = ProviderCommandLine.Lex(command);
        var tokens = lexed.Tokens.Select(t => t.Value).ToArray();
        provider = provider?.Trim().ToLowerInvariant() ?? Infer(tokens[0]);
        if (tokens.Any(t => t is "--help" or "-h" or "--version" or "-V" or "-v") ||
            (tokens.Length > 1 && Utilities.Contains(tokens[1])))
            return new(command, provider, null, null, false, "Utility command; no native conversation is started.");
        if (tokens.Any(t => t is "--no-session-persistence" or "--fork-session" or "--fork"))
            return new(command, provider, null, null, false,
                "This launch forks a conversation or disables persistence; bind the actual new native ID when it is available.");

        if (nativeSessionId is not null) ValidateNativeId(provider, nativeSessionId);
        if (provider is not null && Known.Contains(provider))
        {
            ProviderCommandLine parsed;
            try { parsed = ProviderCommandLine.Parse(command, provider); }
            catch (InvalidOperationException error)
            { return new(command, provider, null, resumeCommand, !string.IsNullOrWhiteSpace(resumeCommand), error.Message); }
            if (parsed.SelectedId is { } selected)
            {
                try { ValidateNativeId(provider, selected); }
                catch (ArgumentException) { return new(command, provider, null, resumeCommand, !string.IsNullOrWhiteSpace(resumeCommand), "Selection is not an exact native ID; bind the actual conversation ID explicitly."); }
                if (nativeSessionId is not null && nativeSessionId != selected)
                    throw new ArgumentException("Command and supplied native conversation IDs differ.");
                nativeSessionId = selected;
            }
            else if (parsed.HasSelection)
                return new(command, provider, null, resumeCommand, !string.IsNullOrWhiteSpace(resumeCommand),
                    "Native ID is unknown for this selection; bind the exact conversation ID or an explicit resume command.");
            else if (provider == "claude")
            {
                nativeSessionId ??= Guid.NewGuid().ToString("D");
                var flag = " --session-id " + nativeSessionId;
                command = parsed.EndOfOptions is { } marker
                    ? command[..marker].TrimEnd() + flag + " " + command[marker..]
                    : command + flag;
            }
            else if (nativeSessionId is not null)
                command = ResumeCommand(parsed.Prefix, provider, nativeSessionId) + parsed.Options;
        }

        var record = new NativeSessionRecord("launch", "launch", command, Environment.CurrentDirectory, provider, nativeSessionId, resumeCommand);
        var capability = GetResumeCapability(record);
        if (capability.CanResume) resumeCommand = PrepareResume(record).Command;
        return new(command, provider, nativeSessionId, resumeCommand, capability.CanResume, capability.Reason);
    }

    /// <summary>
    /// 构造精确的原生恢复命令；身份缺失或含糊时拒绝。
    /// </summary>
    /// <remarks>
    /// 优先使用记录中显式登记的恢复命令；否则对已知 Provider 用记录中的原生标识生成恢复命令
    /// （Codex：<c>resume &lt;id&gt;</c>；OpenCode：<c>--session &lt;id&gt;</c>；Copilot：<c>--resume=&lt;id&gt;</c>；
    /// 其他：<c>--resume &lt;id&gt;</c>），并保留原启动命令中经过验证的选项。
    /// </remarks>
    /// <param name="record">要恢复的持久化记录。</param>
    /// <returns><see cref="ProviderLaunchPlan.CanResume"/> 为 <see langword="true"/> 的计划，命令与恢复命令相同。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">既没有显式恢复命令，也没有可用于已知 Provider 的原生标识；
    /// 或原启动命令无法被受限语法解析。</exception>
    /// <exception cref="ArgumentException">原生标识格式无效，或恢复命令含 NUL。</exception>
    public ProviderLaunchPlan PrepareResume(NativeSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var provider = record.Provider?.ToLowerInvariant() ?? Infer(ProviderCommandLine.Lex(record.Command).Tokens[0].Value);
        string command;
        if (!string.IsNullOrWhiteSpace(record.ResumeCommand)) command = record.ResumeCommand;
        else if (record.NativeSessionId is not null && provider is not null && Known.Contains(provider))
        {
            ValidateNativeId(provider, record.NativeSessionId);
            var parsed = ProviderCommandLine.Parse(record.Command, provider);
            command = ResumeCommand(parsed.Prefix, provider, record.NativeSessionId) + parsed.Options;
        }
        else throw new InvalidOperationException(GetResumeCapability(record).Reason);
        if (command.Contains('\0')) throw new ArgumentException("Resume command cannot contain NUL.");
        return new(command, provider, record.NativeSessionId, command, true, "Exact native resume command is available.");
    }

    /// <summary>
    /// 用记录的原始启动命令开启一个新对话；这个操作刻意与原生恢复区分开。
    /// </summary>
    /// <remarks>
    /// 对已知 Provider，若原命令带有对话选择器，则改用去掉选择器的 <see cref="ProviderCommandLine.FreshCommand"/>，
    /// 然后按 <see cref="PrepareLaunch"/> 的规则准备（Claude 会获得新的会话标识）。
    /// 不会沿用记录中已有的原生标识或恢复命令。
    /// </remarks>
    /// <param name="record">要重新启动的记录，使用其原始启动命令。</param>
    /// <returns>新对话的启动计划。</returns>
    /// <exception cref="InvalidOperationException">已知 Provider 的原启动命令无法被受限语法解析。</exception>
    /// <exception cref="ArgumentException">启动命令为空白或含 NUL。</exception>
    public ProviderLaunchPlan PrepareRestart(NativeSessionRecord record)
    {
        var provider = record.Provider?.ToLowerInvariant() ?? Infer(ProviderCommandLine.Lex(record.Command).Tokens[0].Value);
        var command = record.Command;
        if (provider is not null && Known.Contains(provider))
        {
            var parsed = ProviderCommandLine.Parse(record.Command, provider);
            if (parsed.HasSelection) command = parsed.FreshCommand;
        }
        return PrepareLaunch(command, provider);
    }

    /// <summary>
    /// 判断记录能否恢复，并在身份缺失时如实报告，而不是猜测另一个对话。
    /// </summary>
    /// <remarks>
    /// 显式恢复命令（不含 NUL）总是可用；否则需要已知 Provider、格式正确的原生标识以及可被受限语法解析的原启动命令。
    /// 校验失败时把异常消息作为原因返回，本方法不会因此抛出异常。
    /// </remarks>
    /// <param name="record">要检查的持久化记录。</param>
    /// <returns>能否恢复以及面向用户的原因说明。</returns>
    /// <exception cref="ArgumentException">记录未指定 Provider 且启动命令为空白或含 NUL，无法推断 Provider。</exception>
    public ProviderResumeCapability GetResumeCapability(NativeSessionRecord record)
    {
        var provider = record.Provider?.ToLowerInvariant() ?? Infer(ProviderCommandLine.Lex(record.Command).Tokens[0].Value);
        if (!string.IsNullOrWhiteSpace(record.ResumeCommand)) return record.ResumeCommand.Contains('\0')
            ? new(false, "Resume command cannot contain NUL.") : new(true, "Explicit resume command is registered.");
        if (record.NativeSessionId is not null && provider is not null && Known.Contains(provider))
        {
            try
            {
                ValidateNativeId(provider, record.NativeSessionId);
                _ = ProviderCommandLine.Parse(record.Command, provider);
                return new(true, "Exact native conversation ID and verified launch options are recorded.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return new(false, error.Message); }
        }
        return new(false, Known.Contains(provider ?? "")
            ? "Native conversation ID has not been captured. Bind the exact provider ID before resuming; restarting is a new conversation."
            : "No verified native resume adapter. Register an explicit custom resume command.");
    }

    /// <summary>
    /// 校验原生对话标识是否为该 Provider 接受的精确标识。
    /// </summary>
    /// <remarks>
    /// <para>Claude、Codex、Gemini、Copilot：必须能按 <c>D</c> 格式（<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>）完整解析为 UUID，
    /// 绝不接受名称、前缀或“最近会话”选择器。</para>
    /// <para>其他 Provider：1–256 个字符，以字母或数字开头，只含字母、数字、<c>_</c>、<c>.</c>、<c>:</c>、<c>-</c>，
    /// 不含任何 Shell 语法；OpenCode 还必须以 <c>ses_</c> 开头。</para>
    /// <para>标识会被直接拼接进恢复命令，因此这里的限制同时是防止命令注入的安全边界。</para>
    /// </remarks>
    /// <param name="provider">规范化（小写）的 Provider 标识；未知时为 <see langword="null"/>，按通用规则校验。</param>
    /// <param name="id">要校验的原生对话标识。</param>
    /// <exception cref="ArgumentException">标识不符合上述规则。</exception>
    internal static void ValidateNativeId(string? provider, string id)
    {
        if (provider is "claude" or "codex" or "gemini" or "copilot")
        {
            // Guid.TryParseExact 会忽略首尾空白，长度检查保证标识中没有任何多余字符（如换行），可以安全拼入恢复命令。
            if (id.Length != 36 || !Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("This provider requires a complete native conversation UUID, not a name, prefix, or latest selector.");
        }
        // 用 \z 而不是 $：$ 允许末尾多一个换行。
        else if (id.Length is 0 or > 256 || !Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_.:-]*\z"))
            throw new ArgumentException("Native conversation ID must be an exact identifier without shell syntax.");
        if (provider == "opencode" && !id.StartsWith("ses_", StringComparison.Ordinal))
            throw new ArgumentException("OpenCode native conversation IDs must start with ses_.");
    }

    /// <summary>
    /// 按 Provider 的原生语法生成恢复指定对话的命令（不含其他选项）。
    /// </summary>
    /// <param name="executable">可执行文件的原始拼写（含引号）。</param>
    /// <param name="provider">规范化（小写）的 Provider 标识。</param>
    /// <param name="id">已校验的精确原生对话标识。</param>
    /// <returns>
    /// Codex 为 <c>&lt;exe&gt; resume &lt;id&gt;</c>；OpenCode 为 <c>&lt;exe&gt; --session &lt;id&gt;</c>；
    /// Copilot 为 <c>&lt;exe&gt; --resume=&lt;id&gt;</c>；其他为 <c>&lt;exe&gt; --resume &lt;id&gt;</c>。
    /// </returns>
    private static string ResumeCommand(string executable, string provider, string id) => provider switch
    {
        "codex" => executable + " resume " + id,
        "opencode" => executable + " --session " + id,
        "copilot" => executable + " --resume=" + id,
        _ => executable + " --resume " + id
    };

    /// <summary>
    /// 根据可执行文件名（去掉目录和扩展名，转小写）推断 Provider。
    /// </summary>
    /// <param name="executable">可执行文件参数的值，例如 <c>claude</c> 或 <c>C:\tools\codex.cmd</c>。</param>
    /// <returns>属于已知 Provider 时返回其标识，否则为 <see langword="null"/>。</returns>
    private static string? Infer(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return Known.Contains(name) ? name : null;
    }

}
