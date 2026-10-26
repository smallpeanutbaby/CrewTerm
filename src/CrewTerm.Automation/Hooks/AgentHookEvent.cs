using System.Text.Json;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Providers;

namespace CrewTerm.Automation.Hooks;

/// <summary>
/// 一个已知的 Provider 生命周期事件，只包含状态证据与原生对话身份。
/// </summary>
/// <remarks>
/// 解析时只读取事件名、通知类型、会话标识等少数字段；负载中的提示词与助手回复内容不会被保留。
/// </remarks>
/// <param name="NativeSessionId">事件所属的原生对话标识（已按 Provider 规则校验为精确标识）。</param>
/// <param name="State">事件所表示的语义状态。</param>
/// <param name="Reason">面向用户的说明，形如 <c>Claude Stop: native turn finished</c>。</param>
/// <param name="CanRebind">是否允许把会话重新绑定到这个新的原生对话标识；
/// 仅 Claude 的 <c>SessionStart</c> 为 <see langword="true"/>（对话开始或切换），其他事件来自不同对话时会被忽略。</param>
public sealed record AgentHookEvent(string NativeSessionId, AgentActivityState State, string Reason, bool CanRebind = false)
{
    /// <summary>
    /// 把有文档记载的 Claude 命令 hook 与 Codex notify 事件映射为活动状态；未知事件被忽略。
    /// </summary>
    /// <remarks>
    /// <para>Codex：只识别 <c>type</c> 为 <c>agent-turn-complete</c> 的通知，映射为
    /// <see cref="AgentActivityState.Done"/>，标识取自 <c>thread-id</c>。</para>
    /// <para>Claude（标识取自 <c>session_id</c>）：<c>SessionStart</c> / <c>SessionEnd</c> → 空闲；
    /// <c>UserPromptSubmit</c> → 工作中；<c>PermissionRequest</c> / <c>StopFailure</c> → 等待决定；
    /// <c>Stop</c> → 完成，但 <c>background_tasks</c> 或 <c>session_crons</c> 为非空数组时仍为工作中；
    /// <c>Notification</c> 中 <c>permission_prompt</c> → 等待决定，<c>idle_prompt</c> → 空闲，其他类型忽略。</para>
    /// </remarks>
    /// <param name="provider">Provider 标识（不区分大小写），只支持 <c>claude</c> 与 <c>codex</c>。</param>
    /// <param name="payload">Provider 传来的 JSON 负载，必须是对象。</param>
    /// <returns>识别出的事件；事件类型未知或不影响状态时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentException">负载不是 JSON 对象、Provider 没有经过验证的适配器、
    /// 已识别事件缺少会话标识，或会话标识不是该 Provider 要求的精确格式。</exception>
    public static AgentHookEvent? Parse(string provider, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Hook payload must be a JSON object");
        // 读取字符串属性；属性不存在或不是字符串时返回 null。
        string? Text(string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        // 判断属性是否为非空数组，用于识别 Stop 之后仍在进行的后台任务或定时任务。
        bool Pending(string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
        provider = provider.ToLowerInvariant();
        if (provider == "codex")
        {
            if (Text("type") != "agent-turn-complete") return null;
            var id = Text("thread-id") ?? throw new ArgumentException("Codex notify requires thread-id");
            ProviderSessionService.ValidateNativeId(provider, id);
            return new(id, AgentActivityState.Done, "Codex notify agent-turn-complete: the native turn finished");
        }
        if (provider != "claude") throw new ArgumentException("No verified hook adapter for provider: " + provider);
        var eventName = Text("hook_event_name");
        AgentActivityState? state = eventName switch
        {
            "SessionStart" or "SessionEnd" => AgentActivityState.Idle,
            "UserPromptSubmit" => AgentActivityState.Working,
            "PermissionRequest" or "StopFailure" => AgentActivityState.Blocked,
            "Stop" => Pending("background_tasks") || Pending("session_crons") ? AgentActivityState.Working : AgentActivityState.Done,
            "Notification" => Text("notification_type") switch
            {
                "permission_prompt" => AgentActivityState.Blocked,
                "idle_prompt" => AgentActivityState.Idle,
                _ => null
            },
            _ => null
        };
        if (state is null) return null;
        var nativeId = Text("session_id") ?? throw new ArgumentException("Claude hook requires session_id");
        ProviderSessionService.ValidateNativeId(provider, nativeId);
        var detail = eventName switch
        {
            "Stop" when state == AgentActivityState.Working => "foreground turn stopped; background work or scheduled tasks remain",
            "Stop" => "native turn finished",
            "PermissionRequest" => "permission requested; review the terminal and decide explicitly",
            "StopFailure" => "provider reported a failure; inspect the terminal",
            "SessionEnd" => "native conversation ended or switched; this is not process exit",
            "SessionStart" => "native conversation started or resumed",
            "UserPromptSubmit" => "provider received a prompt",
            _ => "provider notification: " + Text("notification_type")
        };
        return new(nativeId, state.Value, "Claude " + eventName + ": " + detail, eventName == "SessionStart");
    }
}
