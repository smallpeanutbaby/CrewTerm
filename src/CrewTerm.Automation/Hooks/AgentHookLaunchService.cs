using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrewTerm.Automation.Providers;
using CrewTerm.Core.IO;

namespace CrewTerm.Automation.Hooks;

/// <summary>
/// 为单次 Provider 启动生成被动的生命周期回调，绝不修改 Provider 的全局配置。
/// </summary>
/// <remarks>
/// <para>Claude：在状态目录的 <c>provider-hooks</c> 子目录中为会话写一个 <c>&lt;会话标识&gt;.json</c> 设置文件，
/// 并通过 <c>--settings</c> 只对这一次启动生效。Codex：通过 <c>-c notify=[...]</c> 命令行覆盖只对这一次启动生效；
/// 回调命令不带会话标识和状态目录，由 <c>crewterm agent-hook</c> 从终端环境变量
/// （<c>CREWTERM_SESSION_ID</c>、<c>CREWTERM_STATE_DIR</c>）中读取。</para>
/// <para>只要用户已经显式配置了相应机制（Claude 的 <c>--settings</c>、<c>--bare</c>、<c>--safe-mode</c>，
/// 或 Codex 命令行/用户配置中的 <c>notify</c>），或命令无法被可靠解析（包装脚本、Shell 运算符等），
/// 就保留原命令不接入，而不是覆盖用户的设置。</para>
/// </remarks>
public static class AgentHookLaunchService
{
    /// <summary>
    /// 为受支持的启动接入回调，保留用户显式配置的 hook 以及无法确定语义的包装命令。
    /// </summary>
    /// <param name="stateDirectory">CrewTerm 状态目录；Claude 的 hook 设置文件写在其中。</param>
    /// <param name="sessionId">CrewTerm 会话标识，用作 hook 设置文件名，必须是合法且不含目录的文件名。</param>
    /// <param name="executable">回调时调用的 CrewTerm 可执行文件完整路径。</param>
    /// <param name="command">原始启动命令。</param>
    /// <param name="provider">规范化后的 Provider 标识；只有 <c>claude</c> 与 <c>codex</c> 会接入。</param>
    /// <param name="codexHome">Codex 配置目录；为 <see langword="null"/> 时依次使用 <c>CODEX_HOME</c> 环境变量和
    /// <c>%USERPROFILE%\.codex</c>，主要供测试替换。</param>
    /// <returns>本次启动实际使用的命令以及是否接入回调；未接入时命令与原始命令相同。</returns>
    /// <exception cref="ArgumentException">命令为空白或含 NUL；或 Claude 启动时会话标识含有非法文件名字符或目录部分。</exception>
    /// <exception cref="IOException">写入 Claude hook 设置文件失败。</exception>
    public static AgentHookLaunch Prepare(string stateDirectory, string sessionId, string executable, string command, string? provider,
        string? codexHome = null)
    {
        // 生成“不接入”的结果：保持原命令不变，只附带原因说明。
        AgentHookLaunch Skip(string reason) => new(command, false, reason);
        if (provider is not ("claude" or "codex")) return Skip("No automatic hook adapter; use explicit reports or terminal evidence");
        ProviderCommandLine parsed;
        try { parsed = ProviderCommandLine.Parse(command, provider); }
        catch (InvalidOperationException error) { return Skip("Automatic hook not connected: " + error.Message); }
        var tokens = ProviderCommandLine.Lex(command).Tokens.Select(t => t.Value).ToArray();
        // 插入一个选项：命令含 -- 时插在 -- 之前（保证它仍被当作选项而不是提示词），否则追加到末尾。
        string AddOption(string option) => parsed.EndOfOptions is { } marker
            ? command[..marker].TrimEnd() + option + " " + command[marker..] : command + option;
        if (provider == "claude")
        {
            if (tokens.Any(t => t is "--settings" or "--bare" or "--safe-mode" || t.StartsWith("--settings=", StringComparison.Ordinal)))
                return Skip("Existing Claude settings or disabled hooks were preserved; automatic hook is not connected");
            if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(sessionId) != sessionId)
                throw new ArgumentException("Invalid CrewTerm session ID");
            var directory = StateFiles.PathOf(stateDirectory, StateFiles.ProviderHooks);
            var path = Path.Combine(directory, sessionId + ".json");
            if (UnsafeShellPath(path)) return Skip("Hook settings path contains shell expansion syntax; use explicit reports");
            var handler = new
            {
                type = "command", command = executable,
                args = new[] { "--state-dir", Path.GetFullPath(stateDirectory), "agent-hook", "--provider", "claude", "--id", sessionId },
                timeout = 3, onFailure = "continue"
            };
            var hooks = new[] { "SessionStart", "UserPromptSubmit", "PermissionRequest", "Stop", "StopFailure", "Notification", "SessionEnd" }
                .ToDictionary(name => name, _ => new[] { new { hooks = new[] { handler } } });
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hooks })));
            return new(AddOption(" --settings \"" + path + "\""), true, "Claude lifecycle hooks connected for this invocation", path);
        }
        if (tokens.Any(t => Regex.IsMatch(t.Replace("--config=", "", StringComparison.Ordinal).Replace("-c=", "", StringComparison.Ordinal),
                @"^\s*(?:notify|""notify""|'notify')\s*=", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
            return Skip("Existing Codex notify override was preserved; automatic hook is not connected");
        if (NotifyConfigurationReason(codexHome) is { } configuredReason) return Skip(configuredReason);
        if (UnsafeShellPath(executable) || executable.Contains("'''", StringComparison.Ordinal))
            return Skip("Hook executable path cannot be represented safely in the notify invocation; use explicit reports");
        var literal = executable.Contains('\'') ? "'''" + executable + "'''" : "'" + executable + "'";
        var notify = "notify=[" + literal + ",'agent-hook','--provider','codex']";
        return new(AddOption(" -c \"" + notify + "\""), true, "Codex turn-complete notify connected; Working remains inferred from submitted input");
    }

    /// <summary>
    /// 判断路径是否不能安全地嵌入命令行的双引号中。
    /// </summary>
    /// <remarks>
    /// <para>Windows：npm 安装的 Provider CLI 是 <c>.cmd</c> 脚本，会经 <c>cmd.exe</c> 启动；
    /// <c>%</c> 和 <c>!</c> 在 cmd 中即使位于引号内也可能触发变量展开。</para>
    /// <para>Linux / macOS：命令经 <c>/bin/sh</c> 执行，双引号内的 <c>$</c>、反引号与反斜杠仍会被 Shell 解释。</para>
    /// <para>两个平台上 <c>"</c> 都会提前结束引号，换行与 NUL 会截断命令；含有这些字符的路径一律视为不安全。</para>
    /// </remarks>
    /// <param name="path">要嵌入命令行的路径。</param>
    /// <returns>含有当前平台任一不安全字符时返回 <see langword="true"/>。</returns>
    private static bool UnsafeShellPath(string path)
        => path.IndexOfAny(OperatingSystem.IsWindows() ? ['%', '!', '"', '\r', '\n', '\0'] : ['$', '`', '\\', '"', '\r', '\n', '\0']) >= 0;

    /// <summary>
    /// 检查 Codex 用户级与 profile 配置中是否已经配置了 <c>notify</c>；若有则返回不接入的原因。
    /// </summary>
    /// <remarks>
    /// 检查 <c>config.toml</c> 以及配置目录下所有 <c>*.config.toml</c>，用行首的 <c>notify =</c>（可带引号）判断；
    /// 单个文件超过 1 MiB、读取失败或无权限时都保守地视为“已有配置”，不接入。正则匹配超时上限为 1 秒。
    /// </remarks>
    /// <param name="codexHome">Codex 配置目录；为 <see langword="null"/> 时依次使用 <c>CODEX_HOME</c> 环境变量和
    /// <c>%USERPROFILE%\.codex</c>。</param>
    /// <returns>需要保留现有配置时的原因说明；可以安全接入时为 <see langword="null"/>。</returns>
    private static string? NotifyConfigurationReason(string? codexHome)
    {
        codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(codexHome, "config.toml") };
        try
        {
            // Codex 会忽略项目级配置中的 notify。这里保守地保留用户级和 profile 中的回调，
            // 包括由配置文件（而不是命令行）选中的 profile。
            if (Directory.Exists(codexHome))
                foreach (var path in Directory.EnumerateFiles(codexHome, "*.config.toml")) paths.Add(path);
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 1048576) return "Codex config is too large to inspect; existing notify configuration is preserved";
                if (Regex.IsMatch(File.ReadAllText(path), @"^\s*(?:notify|""notify""|'notify')\s*=",
                        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    return "Existing Codex notify in user or profile configuration was preserved; automatic hook is not connected";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        { return "Codex config could not be inspected; existing notify configuration is preserved"; }
        return null;
    }
}
