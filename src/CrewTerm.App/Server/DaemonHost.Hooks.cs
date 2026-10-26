using CrewTerm.App.Protocol;
using CrewTerm.Automation.Hooks;

namespace CrewTerm.App.Server;

/// <remarks>
/// 本分部文件：接收 Provider hook 回调（<c>agent-hook</c>），校验来源后更新会话的语义状态与原生会话标识。
/// </remarks>
public sealed partial class DaemonHost
{
    /// <summary>
    /// 应用一次 Provider hook 回调（<c>agent-hook</c>，由 <c>crewterm agent-hook</c> 命令转发）：把事件映射为语义状态并记录，来源为 <c>hook</c>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>无法识别的事件、已关闭或已退出终端的回调都以成功响应忽略，避免回调进程报错。</description></item>
    /// <item><description>事件的 Provider 必须与会话记录一致。</description></item>
    /// <item><description>事件来自另一个原生对话时：若事件允许重新绑定（Claude <c>SessionStart</c>）或记录尚无原生标识，就把会话绑定到新标识并持久化；否则忽略该事件。</description></item>
    /// </list>
    /// 检查与修改在 <see cref="changeLock"/> 内完成。
    /// </remarks>
    /// <param name="request">请求，需要会话标识、Provider 与 hook 负载。</param>
    /// <returns>记录后的观测，或说明事件被忽略的成功响应。</returns>
    /// <exception cref="ArgumentException">缺少会话标识、Provider 或负载，或 Provider 与记录不一致。</exception>
    /// <exception cref="KeyNotFoundException">运行中的会话没有对应的持久化记录。</exception>
    private DaemonResponse ApplyProviderHook(DaemonRequest request)
    {
        var id = RequiredId(request.SessionId);
        var provider = request.Provider?.ToLowerInvariant() ?? throw new ArgumentException("Hook provider is required");
        var observed = AgentHookEvent.Parse(provider, request.Payload ?? throw new ArgumentException("Hook payload is required"));
        if (observed is null) return new(true, Text: "Unknown provider event ignored");
        lock (changeLock)
        {
            if (!sessions.TryGetValue(id, out var running) || running.Terminal.HasExited)
                return new(true, Text: "Hook for closed terminal ignored");
            var saved = nativeSessions.Get(id) ?? throw new KeyNotFoundException("Saved session not found");
            if (saved.Provider != provider) throw new ArgumentException("Hook provider differs from the recorded provider");
            if (saved.NativeSessionId is not null && saved.NativeSessionId != observed.NativeSessionId && !observed.CanRebind)
                return new(true, Activity: agentActivities.Get(id), Text: "Hook from a different native conversation ignored");
            if (saved.NativeSessionId != observed.NativeSessionId)
            {
                nativeSessions.Bind(id, provider, observed.NativeSessionId);
                SaveTemplates();
            }
            return new(true, Activity: ReportAgent(id, observed.State, "hook", observed.Reason));
        }
    }
}
