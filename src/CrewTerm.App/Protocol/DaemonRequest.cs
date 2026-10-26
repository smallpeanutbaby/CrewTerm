using System.Text.Json;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Tasks;

namespace CrewTerm.App.Protocol;

/// <summary>
/// 发送给 daemon 的一个请求；只有 <see cref="Operation"/> 必填，其余字段按操作取用。
/// </summary>
/// <remarks>
/// 操作分组：会话（<c>ping/list/create/input/screen/resize/close/stop</c>）、后台任务
/// （<c>task-start/pipeline-start/task-list/task-get/task-cancel</c>）、Agent 状态与协作
/// （<c>agent-*</c>、<c>notification*</c>、<c>message-*</c>）、原生会话（<c>session-*</c>）和本地插件（<c>plugin-*</c>）。
/// 新增字段必须是可选的，并且默认值保持旧版本的行为。
/// </remarks>
/// <param name="Operation">操作名，例如 <c>create</c>、<c>screen</c>、<c>agent-wait</c>。</param>
/// <param name="SessionId">目标会话或任务标识。</param>
/// <param name="Command">要启动的命令行（<c>create</c>、<c>task-start</c>）。</param>
/// <param name="WorkingDirectory">命令的工作目录；省略时使用状态目录。</param>
/// <param name="Text">要写入终端的原始文本、提示词或消息正文。</param>
/// <param name="Columns">终端列数（<c>create</c>、<c>resize</c>、会话恢复）。</param>
/// <param name="Rows">终端行数（<c>create</c>、<c>resize</c>、会话恢复）。</param>
/// <param name="Name">会话、任务或流水线的显示名称。</param>
/// <param name="Steps">流水线步骤（<c>pipeline-start</c>）。</param>
/// <param name="State">要报告或等待的单个 Agent 状态。</param>
/// <param name="States">等待其中任一状态（<c>agent-wait</c>）。</param>
/// <param name="Source">状态报告的来源说明，例如 <c>api</c>。</param>
/// <param name="Reason">状态报告的原因说明。</param>
/// <param name="TimeoutMilliseconds"><c>agent-wait</c> 的等待超时（1–60000 毫秒）。</param>
/// <param name="AfterRevision">只接受版本号大于该值的状态变化（<c>agent-wait</c>）。</param>
/// <param name="FromSessionId">消息或交接的发送方会话。</param>
/// <param name="TargetSessionId">消息或交接的接收方会话。</param>
/// <param name="NotificationId">要标记已读的通知标识。</param>
/// <param name="MessageId">要确认的消息标识。</param>
/// <param name="InReplyTo">回复的原消息标识。</param>
/// <param name="Provider">Agent Provider，例如 <c>claude</c>、<c>codex</c>。</param>
/// <param name="NativeSessionId">Provider 的原生会话标识（必须是完整、精确的标识）。</param>
/// <param name="ResumeCommand">显式登记的恢复命令。</param>
/// <param name="PluginId">本地插件标识。</param>
/// <param name="PluginCommand">插件中声明的命令标识。</param>
/// <param name="Path">本地插件目录（<c>plugin-install</c>）。</param>
/// <param name="Enabled">插件的启用状态。</param>
/// <param name="EventName">插件事件名（<c>plugin-event</c>）。</param>
/// <param name="Payload">传给插件或 hook 的 JSON 数据。</param>
/// <param name="IncludeText">
/// <c>screen</c> 响应是否附带整屏纯文本。UI 轮询只需要单元格，传 <see langword="false"/> 可减少一半以上的传输量；
/// 默认 <see langword="true"/>，与旧客户端行为一致。
/// </param>
public sealed record DaemonRequest(string Operation, string? SessionId = null, string? Command = null,
    string? WorkingDirectory = null, string? Text = null, int Columns = 100, int Rows = 30, string? Name = null,
    CommandTask[]? Steps = null, AgentActivityState? State = null, AgentActivityState[]? States = null,
    string? Source = null, string? Reason = null, int TimeoutMilliseconds = 10000, long? AfterRevision = null,
    string? FromSessionId = null, string? TargetSessionId = null, string? NotificationId = null,
    string? MessageId = null, string? InReplyTo = null, string? Provider = null, string? NativeSessionId = null,
    string? ResumeCommand = null, string? PluginId = null, string? PluginCommand = null, string? Path = null,
    bool? Enabled = null, string? EventName = null, JsonElement? Payload = null, bool IncludeText = true);
