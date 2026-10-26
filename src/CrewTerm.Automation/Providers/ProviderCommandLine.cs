using System.Text;

namespace CrewTerm.Automation.Providers;

/// <summary>
/// Provider CLI 启动命令的解析结果：可执行文件、需要保留的已知选项、对话选择器以及初始提示词。
/// </summary>
/// <remarks>
/// <para>这是一个刻意受限的 CLI 语法，而不是 Shell 解析器：只接受已知的选项和子命令，
/// 遇到未知选项、Shell 运算符、多行命令、不平衡的引号或多个位置参数时一律拒绝
/// （抛出 <see cref="InvalidOperationException"/>，提示用户登记显式的恢复命令），而不是猜测其含义。</para>
/// <para>已知选项保留原始拼写（含引号），因此带引号的路径或配置值在重组命令时绝不会被静默规范化或丢弃。</para>
/// </remarks>
internal sealed class ProviderCommandLine
{
    /// <summary>
    /// Codex 中需要一个值、且在恢复或重新启动时应原样保留的选项（配置、模型、profile、沙箱、工作目录、审批策略等）。
    /// </summary>
    private static readonly HashSet<string> CodexValues = ["-c", "--config", "--enable", "--disable", "-m", "--model", "--local-provider", "-p", "--profile", "-s", "--sandbox", "-C", "--cd", "--add-dir", "-a", "--ask-for-approval"];
    /// <summary>
    /// Codex 中不带值、且应原样保留的开关选项；带 <c>=值</c> 的写法不被接受。
    /// </summary>
    private static readonly HashSet<string> CodexFlags = ["--strict-config", "--oss", "--approve-for-me", "--dangerously-bypass-approvals-and-sandbox", "--dangerously-bypass-hook-trust", "--search", "--no-alt-screen", "--no-daemon"];
    /// <summary>
    /// Claude Code 中需要一个值、且应原样保留的选项（模型、权限模式、系统提示词、设置文件、插件目录等）。
    /// </summary>
    private static readonly HashSet<string> ClaudeValues = ["--agent", "--agents", "--append-system-prompt", "--autocompact", "--debug-file", "--effort", "--fallback-model", "--input-format", "--json-schema", "--max-budget-usd", "--model", "-n", "--name", "--output-format", "--permission-mode", "--permission-prompts", "--plugin-dir", "--plugin-url", "--setting-sources", "--settings", "--system-prompt", "--system-prompt-snapshot"];
    /// <summary>
    /// Claude Code 中不带值、且应原样保留的开关选项；带 <c>=值</c> 的写法不被接受。
    /// </summary>
    private static readonly HashSet<string> ClaudeFlags = ["--allow-dangerously-skip-permissions", "--ax-screen-reader", "--bare", "--brief", "--chrome", "--dangerously-skip-permissions", "--disable-slash-commands", "--exclude-dynamic-system-prompt-sections", "--forward-subagent-text", "--ide", "--include-hook-events", "--include-partial-messages", "--no-chrome", "-p", "--print", "--replay-user-messages", "--restricted", "--safe-mode", "--strict-mcp-config", "--verbose"];
    /// <summary>
    /// 不会开启交互式对话的 Codex 子命令；作为第一个位置参数出现时拒绝解析。
    /// <c>resume</c> 不在其中，它作为对话选择器单独处理。
    /// </summary>
    private static readonly HashSet<string> CodexCommands = ["agents", "exec", "e", "review", "login", "logout", "mcp", "plugin", "app-server", "remote-control", "app", "completion", "update", "doctor", "sandbox", "debug", "apply", "queue", "archive", "delete", "migrate-rollouts", "unarchive", "fork", "cloud", "exec-server", "features", "help"];
    /// <summary>
    /// 不会开启交互式对话的 Claude Code 子命令；作为第一个位置参数出现时拒绝解析。
    /// </summary>
    private static readonly HashSet<string> ClaudeCommands = ["agents", "attach", "auth", "auto-mode", "doctor", "gateway", "import", "install", "logs", "mcp", "plugin", "plugins", "purge", "respawn", "rm", "setup-token", "stop", "kill", "ultrareview", "update", "upgrade"];
    /// <summary>
    /// 需要保留的已知选项及其值的原始拼写（含引号），按在命令中出现的顺序排列；对话选择器不在其中。
    /// </summary>
    private readonly List<string> options = [];

    /// <summary>
    /// 对话选择器给出的值（已去掉引号），例如 <c>--resume &lt;id&gt;</c>、<c>--session-id &lt;id&gt;</c>、
    /// <c>codex resume &lt;id&gt;</c> 中的标识；可能出现多次。
    /// </summary>
    private readonly List<string> selections = [];

    /// <summary>
    /// 唯一的位置参数，即初始提示词；没有时为 <see langword="null"/>。
    /// </summary>
    private CommandToken? prompt;

    /// <summary>
    /// 是否出现了 <c>--last</c>、<c>--continue</c> 或 <c>-c</c>（Codex 除外）这类“最近会话”选择器；
    /// 一旦出现，<see cref="SelectedId"/> 永远为 <see langword="null"/>，绝不把它当作精确身份。
    /// </summary>
    private bool ambiguousSelection;

    /// <summary>
    /// 可执行文件参数的原始拼写（含引号），作为重组命令的开头。
    /// </summary>
    public required string Prefix { get; init; }

    /// <summary>
    /// 命令是否选择了一个已有对话（恢复、继续或指定会话标识的选项，或 Codex 的 <c>resume</c> 子命令），
    /// 无论能否得到精确标识。
    /// </summary>
    public bool HasSelection { get; private set; }

    /// <summary>
    /// <c>--</c> 参数在原命令字符串中的起始字符位置；命令中没有 <c>--</c> 时为 <see langword="null"/>。
    /// 需要追加选项时插入到这个位置之前，以免被当作提示词。
    /// </summary>
    public int? EndOfOptions { get; private set; }

    /// <summary>
    /// 命令选中的精确对话标识：仅当没有“最近会话”选择器且所有选择器的值（序数比较）都相同时才有值，否则为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 这里只保证“唯一且明确”，并不校验格式；调用方还需用 <see cref="ProviderSessionService.ValidateNativeId"/> 校验。
    /// </remarks>
    public string? SelectedId => !ambiguousSelection && selections.Distinct(StringComparer.Ordinal).Count() == 1 ? selections[0] : null;

    /// <summary>
    /// 需要保留的选项按原始拼写以空格连接、并带一个前导空格的文本；没有选项时为空字符串，可直接拼接在 <see cref="Prefix"/> 之后。
    /// </summary>
    public string Options => options.Count == 0 ? "" : " " + string.Join(' ', options);

    /// <summary>
    /// 去掉所有对话选择器后开启新对话的命令：可执行文件、保留的选项，以及原始的初始提示词
    /// （原命令含 <c>--</c> 时提示词前保留 <c>--</c>）。
    /// </summary>
    public string FreshCommand => Prefix + Options + (prompt is null ? "" : (EndOfOptions is null ? " " : " -- ") + prompt.Raw);

    /// <summary>
    /// 按指定 Provider 的受限语法解析启动命令。
    /// </summary>
    /// <remarks>
    /// <para>可执行文件的文件名（去掉扩展名）必须与 Provider 相同（不区分大小写），否则视为包装脚本而拒绝。</para>
    /// <para>对 <c>claude</c> 与 <c>codex</c> 以外的 Provider 没有可保留的选项列表，除对话选择器外的任何选项都会被拒绝。</para>
    /// <para><c>--</c> 之后的参数不再被当作选项或子命令；位置参数至多一个，即初始提示词。</para>
    /// </remarks>
    /// <param name="command">完整的启动命令。</param>
    /// <param name="provider">规范化（小写）的 Provider 标识。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="InvalidOperationException">命令含 Shell 运算符、换行或不平衡的引号，可执行文件与 Provider 不符，
    /// 出现未知选项、缺值或多余值的选项、非交互子命令或多个位置参数。</exception>
    /// <exception cref="ArgumentException">命令为空白或含 NUL。</exception>
    public static ProviderCommandLine Parse(string command, string provider)
    {
        var lexed = Lex(command);
        if (lexed.UnsafeReason is not null) throw Unsupported(lexed.UnsafeReason);
        var tokens = lexed.Tokens;
        if (!Path.GetFileNameWithoutExtension(tokens[0].Value).Equals(provider, StringComparison.OrdinalIgnoreCase))
            throw Unsupported("The executable is a wrapper or differs from the provider");
        var result = new ProviderCommandLine { Prefix = tokens[0].Raw };
        var resumeSubcommand = false;
        var resumeIdConsumed = false;
        var afterEnd = false;
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var value = token.Value;
            if (!afterEnd && value == "--") { afterEnd = true; result.EndOfOptions = token.Start; continue; }
            if (!afterEnd && value.StartsWith('-'))
            {
                var equal = value.IndexOf('=');
                var option = equal < 0 ? value : value[..equal];
                if (IsSelector(provider, option, out var takesValue, out var optional))
                {
                    result.HasSelection = true;
                    if (option is "--last" or "--continue" or "-c") result.ambiguousSelection = true;
                    if (!takesValue) { if (equal >= 0) throw Unsupported("Unexpected value for " + option); continue; }
                    if (equal >= 0) result.selections.Add(value[(equal + 1)..]);
                    else if (index + 1 < tokens.Count && !tokens[index + 1].Value.StartsWith('-')) result.selections.Add(tokens[++index].Value);
                    else if (!optional) throw Unsupported("Missing value for " + option);
                    continue;
                }
                var values = provider == "codex" ? CodexValues : provider == "claude" ? ClaudeValues : [];
                var flags = provider == "codex" ? CodexFlags : provider == "claude" ? ClaudeFlags : [];
                if (values.Contains(option))
                {
                    result.options.Add(token.Raw);
                    if (equal >= 0) { if (equal == value.Length - 1) throw Unsupported("Missing value for " + option); }
                    else
                    {
                        if (index + 1 >= tokens.Count || tokens[index + 1].Value.StartsWith('-')) throw Unsupported("Missing value for " + option);
                        result.options.Add(tokens[++index].Raw);
                    }
                }
                else if (flags.Contains(option) && equal < 0) result.options.Add(token.Raw);
                else throw Unsupported("Unsupported or ambiguous option " + option);
                continue;
            }
            if (!afterEnd && provider == "codex" && !resumeSubcommand && result.prompt is null && value == "resume")
            {
                resumeSubcommand = true;
                result.HasSelection = true;
                continue;
            }
            // “--” 之后的内容是提示词，不能被当作 resume 选中的会话标识。
            if (resumeSubcommand && !resumeIdConsumed && !afterEnd)
            {
                result.selections.Add(value);
                resumeIdConsumed = true;
                continue;
            }
            if (!afterEnd && result.prompt is null && ((provider == "codex" && CodexCommands.Contains(value)) || (provider == "claude" && ClaudeCommands.Contains(value))))
                throw Unsupported("This subcommand does not start an interactive conversation");
            if (result.prompt is not null) throw Unsupported("Multiple positional arguments cannot be safely separated from the initial prompt");
            result.prompt = token;
        }
        return result;
    }

    /// <summary>
    /// 判断选项是否为选择已有对话的选择器，并给出它是否带值、值是否可省略。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Codex：<c>--last</c>、<c>--all</c>、<c>--include-non-interactive</c>（<c>resume</c> 子命令的开关，不带值）。</description></item>
    /// <item><description>其他 Provider：<c>--continue</c> / <c>-c</c>（不带值）。</description></item>
    /// <item><description>OpenCode：<c>--session</c> / <c>-s</c>，值必填。</description></item>
    /// <item><description>Claude、Gemini、Copilot 等：<c>--resume</c> / <c>-r</c>（值可省略，省略时进入交互选择）以及 <c>--session-id</c>（值必填）。</description></item>
    /// </list>
    /// </remarks>
    /// <param name="provider">规范化（小写）的 Provider 标识。</param>
    /// <param name="option">去掉 <c>=值</c> 部分后的选项名。</param>
    /// <param name="takesValue">选择器是否带值；仅在返回 <see langword="true"/> 时有意义。</param>
    /// <param name="optional">值是否可以省略；仅在返回 <see langword="true"/> 时有意义。</param>
    /// <returns>是对话选择器时返回 <see langword="true"/>。</returns>
    private static bool IsSelector(string provider, string option, out bool takesValue, out bool optional)
    {
        optional = false;
        takesValue = false;
        if (provider == "codex") return option is "--last" or "--all" or "--include-non-interactive";
        if (option is "--continue" or "-c") return true;
        if (provider == "opencode") { takesValue = true; return option is "--session" or "-s"; }
        takesValue = true;
        optional = option is "--resume" or "-r";
        return option is "--resume" or "-r" or "--session-id";
    }

    /// <summary>
    /// 按 Windows 命令行参数规则（与 <c>CommandLineToArgvW</c> 的引号和反斜杠规则一致）把命令切分为参数，
    /// 同时标记无法安全重组的写法。
    /// </summary>
    /// <remarks>
    /// <para>引号与反斜杠：紧跟在 <c>"</c> 之前的 n 个反斜杠变为 n/2 个；n 为偶数时该 <c>"</c> 切换引号状态，
    /// 为奇数时成为字面的 <c>"</c>；不在 <c>"</c> 之前的反斜杠原样保留。任何空白字符（引号外）都分隔参数。</para>
    /// <para>以下写法不会导致异常，而是通过 <c>UnsafeReason</c> 报告（多处命中时保留最后一个原因）：
    /// 引号外的 <c>&amp;</c>、<c>|</c>、<c>&lt;</c>、<c>&gt;</c>、<c>^</c>、<c>(</c>、<c>)</c>；回车或换行；以及不平衡的引号。</para>
    /// <para>Linux / macOS 上命令经 <c>/bin/sh</c> 执行，Shell 的展开与引号规则和上面的规则不同，因此还会报告：
    /// 任意位置（双引号内仍会被 Shell 解释）的 <c>$</c>、反引号与反斜杠，以及引号外的单引号、<c>;</c>、<c>*</c>、<c>?</c>、
    /// <c>[</c>、<c>]</c>、<c>~</c>、<c>#</c>、<c>{</c>、<c>}</c>。常见的“程序 + 选项 + 标识”命令不含这些字符，不受影响；
    /// 双引号内的单引号（如 <c>-c "model='x y'"</c>）按字面处理，同样不受影响。</para>
    /// </remarks>
    /// <param name="command">完整的命令文本。</param>
    /// <returns>
    /// <c>Tokens</c>：按出现顺序排列的参数（至少一个），每个包含去引号后的值、原始拼写与起始位置；
    /// <c>UnsafeReason</c>：命令无法被安全解析时的原因说明，安全时为 <see langword="null"/>。
    /// </returns>
    /// <exception cref="ArgumentException">命令为空白或含 NUL。</exception>
    internal static (List<CommandToken> Tokens, string? UnsafeReason) Lex(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\0')) throw new ArgumentException("Command cannot contain NUL.");
        var tokens = new List<CommandToken>();
        string? unsafeReason = null;
        for (var index = 0; index < command.Length;)
        {
            if (char.IsWhiteSpace(command[index])) { if (command[index] is '\r' or '\n') unsafeReason = "Multiline commands require an explicit resume command"; index++; continue; }
            var start = index;
            var quoted = false;
            var value = new StringBuilder();
            while (index < command.Length && (quoted || !char.IsWhiteSpace(command[index])))
            {
                var c = command[index];
                if (!quoted && c is '&' or '|' or '<' or '>' or '^' or '(' or ')') unsafeReason = "Shell operators require an explicit resume command";
                // Linux / macOS 的命令经 /bin/sh 执行：这些字符会触发展开、注释或另一套引号与转义规则，与此处的解析不一致。
                if (!OperatingSystem.IsWindows() && (c is '$' or '`' or '\\' || !quoted && c is '\'' or ';' or '*' or '?' or '[' or ']' or '~' or '#' or '{' or '}'))
                    unsafeReason = "Shell expansion syntax requires an explicit resume command";
                if (c is '\r' or '\n') unsafeReason = "Multiline commands require an explicit resume command";
                if (c == '\\')
                {
                    var slashStart = index;
                    while (index < command.Length && command[index] == '\\') index++;
                    var count = index - slashStart;
                    if (index < command.Length && command[index] == '"')
                    {
                        value.Append('\\', count / 2);
                        if (count % 2 == 0) quoted = !quoted; else value.Append('"');
                        index++;
                    }
                    else value.Append('\\', count);
                }
                else if (c == '"') { quoted = !quoted; index++; }
                else { value.Append(c); index++; }
            }
            if (quoted) unsafeReason = "Unbalanced quoting requires an explicit resume command";
            tokens.Add(new(value.ToString(), command[start..index], start));
        }
        return (tokens, unsafeReason);
    }

    /// <summary>
    /// 创建“无法安全解析”异常，消息末尾提示用户登记显式的恢复命令以保留原本的调用方式。
    /// </summary>
    /// <param name="reason">具体原因（英文，面向用户）。</param>
    /// <returns>待抛出的异常；本方法本身不抛出。</returns>
    private static InvalidOperationException Unsupported(string reason) => new(reason + "; register an explicit resume command to preserve the intended invocation.");
}
