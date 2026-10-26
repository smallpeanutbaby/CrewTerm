using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Plugins;
using CrewTerm.Terminal.Emulation;

namespace CrewTerm.App.Protocol;

/// <summary>
/// daemon 对一个请求的响应；业务失败通过 <see cref="Ok"/> 与 <see cref="Error"/> 表示，其余字段按操作填写。
/// </summary>
/// <param name="Ok">请求是否成功。</param>
/// <param name="Error">失败原因；成功时为 <see langword="null"/>。</param>
/// <param name="SessionId">新建或受影响的会话、任务标识。</param>
/// <param name="Sessions">会话列表（<c>list</c>、<c>session-catalog</c>）。</param>
/// <param name="Screen">屏幕快照（<c>screen</c>）。</param>
/// <param name="Text">整屏纯文本，或给用户的说明文字。</param>
/// <param name="Tasks">任务列表（不含输出）。</param>
/// <param name="Task">单个任务的快照（含输出）。</param>
/// <param name="Activity">单个会话的 Agent 状态。</param>
/// <param name="Activities">全部会话的 Agent 状态，按优先级排序。</param>
/// <param name="Notifications">通知列表。</param>
/// <param name="Message">刚发送的消息。</param>
/// <param name="Messages">收件箱中的消息。</param>
/// <param name="Delivered">提示词是否已写入目标终端。</param>
/// <param name="Plugin">单个插件。</param>
/// <param name="Plugins">已登记的插件列表。</param>
/// <param name="PluginResult">一次插件命令的执行结果。</param>
/// <param name="PluginResults">一次事件触发的全部插件结果，或插件执行历史。</param>
/// <param name="Version">daemon 的版本号（<c>ping</c>）。</param>
/// <param name="ProtocolVersion">daemon 支持的协议版本（<c>ping</c>）；旧版 daemon 不返回。</param>
public sealed record DaemonResponse(bool Ok, string? Error = null, string? SessionId = null,
    SessionInfo[]? Sessions = null, TerminalSnapshot? Screen = null, string? Text = null,
    DaemonTaskInfo[]? Tasks = null, DaemonTaskInfo? Task = null, AgentActivity? Activity = null,
    AgentActivity[]? Activities = null, AgentNotification[]? Notifications = null, AgentMessage? Message = null,
    AgentMessage[]? Messages = null, bool? Delivered = null, LocalPluginInfo? Plugin = null,
    LocalPluginInfo[]? Plugins = null, LocalPluginResult? PluginResult = null, LocalPluginResult[]? PluginResults = null,
    string? Version = null, int? ProtocolVersion = null);
