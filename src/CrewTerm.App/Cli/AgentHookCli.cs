using System.Text;
using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.Automation.Hooks;

namespace CrewTerm.App.Cli;

/// <summary>
/// <c>crewterm agent-hook</c> 子命令：Provider（Claude hook、Codex notify）在 CrewTerm 终端中触发的被动回调，
/// 把事件负载转发给所属 daemon 的 <c>agent-hook</c> 操作。
/// </summary>
/// <remarks>
/// 被动回调只连接拥有该会话的 daemon。它绝不能向标准输出打印审批 JSON（Provider 会把它当作对工具调用的决定），
/// 绝不能在 daemon 关闭期间启动另一个 daemon（因此直接使用 <see cref="DaemonClient"/> 而不经过启动器），
/// 也绝不保留负载中的提示词或助手回复内容。
/// </remarks>
internal static class AgentHookCli
{
    /// <summary>
    /// 读取一次 hook 负载并转发给 daemon。
    /// </summary>
    /// <remarks>
    /// <para>Provider、状态目录与会话标识依次取 <c>--provider</c>、<c>--state-dir</c>、<c>--id</c> 选项，
    /// 缺省时取终端环境变量 <c>CREWTERM_PROVIDER</c>、<c>CREWTERM_STATE_DIR</c>、<c>CREWTERM_SESSION_ID</c>。</para>
    /// <para>负载取自 <c>--json</c> 选项；Codex 的 notify 把 JSON 作为唯一的位置参数传入；其他情况从标准输入读到结束。
    /// 负载最多 65536 个字符。读取与调用 daemon 共用 2.5 秒的期限，以免拖慢 Provider。
    /// 无法识别的事件不联系 daemon，直接成功返回。</para>
    /// </remarks>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>进程退出码 0。</returns>
    /// <exception cref="ArgumentException">无法确定 Provider、状态目录或会话标识。</exception>
    /// <exception cref="InvalidDataException">负载超过 65536 个字符。</exception>
    /// <exception cref="JsonException">负载不是有效的 JSON。</exception>
    /// <exception cref="OperationCanceledException">2.5 秒内没有完成读取与转发。</exception>
    /// <exception cref="InvalidOperationException">daemon 返回失败。</exception>
    public static async Task<int> RunAsync(CrewTermCommandLine options)
    {
        var provider = options.Get("provider") ?? Environment.GetEnvironmentVariable("CREWTERM_PROVIDER")
            ?? throw new ArgumentException("Hook provider is required");
        var stateDirectory = options.Get("state-dir") ?? Environment.GetEnvironmentVariable("CREWTERM_STATE_DIR")
            ?? throw new ArgumentException("Hook state directory is required");
        var id = options.Get("id") ?? Environment.GetEnvironmentVariable("CREWTERM_SESSION_ID")
            ?? throw new ArgumentException("Hook session ID is required");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        var text = options.Get("json") ?? (provider == "codex" ? options.Positionals.SingleOrDefault() : null);
        if (text is null)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            var buffer = new char[4096];
            var content = new StringBuilder();
            int count;
            while ((count = await Console.In.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
            {
                if (content.Length + count > 65536) throw new InvalidDataException("Hook payload exceeds 65536 characters");
                content.Append(buffer, 0, count);
            }
            text = content.ToString();
        }
        if (text.Length > 65536) throw new InvalidDataException("Hook payload exceeds 65536 characters");
        var payload = JsonSerializer.Deserialize<JsonElement>(text);
        if (AgentHookEvent.Parse(provider, payload) is null) return 0;
        var response = await new DaemonClient(stateDirectory).CallAsync(new("agent-hook", id, Provider: provider, Payload: payload), deadline.Token);
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        return 0;
    }
}
