using System.Globalization;
using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.Cli;

/// <summary>
/// 把非交互子命令（<c>sessions</c>、<c>send</c>、<c>run</c>、<c>agent-*</c>、<c>plugin-*</c> 等）转换为 daemon 请求并输出 JSON 结果。
/// </summary>
/// <remarks>
/// 参数在连接或启动 daemon 之前全部校验完毕：拼写错误或缺少参数的命令不会产生任何副作用。
/// 相对路径（<c>--project</c>、<c>--path</c>）按调用方的当前目录转换为绝对路径后再发送，因为 daemon 的当前目录是程序所在目录。
/// 成功时向标准输出写入缩进 JSON（<c>read</c> 只写屏幕文本）并返回 0；失败抛出异常，由入口打印并返回 1。
/// </remarks>
internal static class CliCommands
{
    /// <summary>
    /// 缩进输出的 JSON 选项。
    /// </summary>
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    /// <summary>
    /// 根据命令行构造 daemon 请求，并校验所有必填参数和取值格式。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>要发送的请求。</returns>
    /// <exception cref="ArgumentException">未知子命令、缺少目标标识或必填选项、取值格式错误。</exception>
    internal static DaemonRequest BuildRequest(CrewTermCommandLine options)
    {
        var id = options.Get("id") ?? options.Positionals.FirstOrDefault();
        // 需要明确目标的命令缺少标识时立即报错，而不是让 daemon 回答“会话不存在”。
        string Id() => !string.IsNullOrWhiteSpace(id) ? id
            : throw new ArgumentException($"'{options.Command}' requires a target ID (positional argument or --id).");
        string Required(string key) => options.Get(key) ?? throw new ArgumentException("Required: --" + key);
        return options.Command switch
        {
            "sessions" => new(ParseBool(options, "all", false) ? "session-catalog" : "list"),
            "new" => new("create", Command: Required("command"), WorkingDirectory: Path.GetFullPath(options.Get("project") ?? Environment.CurrentDirectory),
                Name: options.Get("name"), Provider: options.Get("provider"), NativeSessionId: options.Get("native-id"), ResumeCommand: options.Get("resume-command")),
            "send" => new("input", SessionId: Id(), Text: Required("text")),
            "read" => new("screen", SessionId: Id()),
            "close" => new("close", SessionId: Id()),
            "stop" => new("stop"),
            "tasks" => new("task-list"),
            "run" => new("task-start", Command: Required("command"), WorkingDirectory: Path.GetFullPath(options.Get("project") ?? Environment.CurrentDirectory), Name: options.Get("name") ?? "Command"),
            "task" => new("task-get", SessionId: Id()),
            "cancel" => new("task-cancel", SessionId: Id()),
            "agent-state" => new("agent-state", SessionId: id),
            "agent-report" => new("agent-report", SessionId: Id(), State: ParseAgentState(Required("state")),
                Source: options.Get("source") ?? "api", Reason: options.Get("reason") ?? "Explicit CLI report"),
            "agent-prompt" => new("agent-prompt", SessionId: Id(), Text: Required("text")),
            "agent-wait" => new("agent-wait", SessionId: Id(),
                States: Required("state").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseAgentState).ToArray(),
                TimeoutMilliseconds: ParseInt(options, "timeout-ms", 10000),
                AfterRevision: options.Get("after-revision") is { } revision ? ParseLong("after-revision", revision) : null),
            "notifications" => new("notifications", SessionId: id),
            "notification-read" => new("notification-read", NotificationId: Id()),
            "message-send" or "agent-handoff" => new(options.Command, FromSessionId: Required("from"),
                TargetSessionId: Required("to"), Text: Required("text"), InReplyTo: options.Get("reply-to")),
            "message-list" => new("message-list", SessionId: Id()),
            "message-ack" => new("message-ack", SessionId: Id(), MessageId: Required("message")),
            "session-bind" => new("session-bind", SessionId: Id(), Provider: Required("provider"), NativeSessionId: options.Get("native-id"), ResumeCommand: options.Get("resume-command")),
            "session-resume" or "session-restart" or "session-forget" => new(options.Command, SessionId: Id()),
            "plugins" => new("plugin-list"),
            "plugin-install" => new("plugin-install", Path: Path.GetFullPath(Required("path")), Enabled: ParseBool(options, "enabled", false)),
            "plugin-enable" => new("plugin-enable", PluginId: Id(), Enabled: ParseBool(options, "enabled", null)),
            "plugin-remove" => new("plugin-remove", PluginId: Id()),
            "plugin-run" => new("plugin-run", PluginId: Id(), PluginCommand: Required("command"), Payload: ParsePayload(options.Get("json"))),
            "plugin-event" => new("plugin-event", EventName: Required("event"), Payload: ParsePayload(options.Get("json"))),
            "plugin-history" => new("plugin-history"),
            _ => throw new ArgumentException("Unknown command: " + options.Command + ". Use --help.")
        };
    }

    /// <summary>
    /// 发送请求并把结果写到标准输出。
    /// </summary>
    /// <param name="options">已解析的命令行（含握手得到的兼容性警告）。</param>
    /// <param name="request">已校验的请求。</param>
    /// <returns>进程退出码 0。</returns>
    /// <exception cref="InvalidOperationException">daemon 版本过旧不支持该操作，或 daemon 返回失败。</exception>
    internal static async Task<int> ExecuteAsync(CrewTermCommandLine options, DaemonRequest request)
    {
        if (options.ServerCompatibilityWarning is { } warning && CrewTermVersion.RequiresCurrentProtocol(request))
            throw new InvalidOperationException(warning);
        var response = await new DaemonClient(options.StateDirectory).CallAsync(request);
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        Console.WriteLine(options.Command == "read" ? response.Text : JsonSerializer.Serialize(response, Output));
        return 0;
    }

    /// <summary>
    /// 解析 Agent 状态名（不区分大小写）。
    /// </summary>
    /// <param name="value">状态名，例如 <c>done</c>。</param>
    /// <returns>对应的状态。</returns>
    /// <exception cref="ArgumentException">不是已定义的状态名。</exception>
    private static AgentActivityState ParseAgentState(string value)
        => !int.TryParse(value, out _) && Enum.TryParse<AgentActivityState>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Unknown Agent state: " + value);

    /// <summary>
    /// 解析 <c>--json</c> 选项。
    /// </summary>
    /// <param name="value">JSON 文本；未提供时为 <see langword="null"/>。</param>
    /// <returns>解析后的 JSON 值。</returns>
    /// <exception cref="ArgumentException">不是有效的 JSON。</exception>
    private static JsonElement? ParsePayload(string? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException error) { throw new ArgumentException("--json must be valid JSON: " + error.Message, error); }
    }

    /// <summary>
    /// 解析整数选项。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <param name="name">选项名（不含 <c>--</c>）。</param>
    /// <param name="fallback">未提供时的默认值。</param>
    /// <returns>选项值。</returns>
    /// <exception cref="ArgumentException">不是十进制整数。</exception>
    private static int ParseInt(CrewTermCommandLine options, string name, int fallback)
        => options.Get(name) is not { } text ? fallback
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
            : throw new ArgumentException($"--{name} must be an integer");

    /// <summary>
    /// 解析 64 位整数选项。
    /// </summary>
    /// <param name="name">选项名（不含 <c>--</c>），用于错误信息。</param>
    /// <param name="text">选项值。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentException">不是十进制整数。</exception>
    private static long ParseLong(string name, string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
            : throw new ArgumentException($"--{name} must be an integer");

    /// <summary>
    /// 解析布尔选项（<c>true</c>/<c>false</c>，不区分大小写）。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <param name="name">选项名（不含 <c>--</c>）。</param>
    /// <param name="fallback">未提供时的默认值；为 <see langword="null"/> 表示该选项必填。</param>
    /// <returns>选项值。</returns>
    /// <exception cref="ArgumentException">选项必填却未提供，或取值不是 <c>true</c>/<c>false</c>。</exception>
    private static bool ParseBool(CrewTermCommandLine options, string name, bool? fallback)
    {
        if (options.Get(name) is not { } text)
            return fallback ?? throw new ArgumentException("Required: --" + name);
        return bool.TryParse(text, out var value) ? value : throw new ArgumentException($"--{name} must be true or false");
    }
}
