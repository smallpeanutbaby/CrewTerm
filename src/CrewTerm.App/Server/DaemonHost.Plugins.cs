using System.Text.Json;
using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.App.Server;

/// <remarks>
/// 本分部文件：本地插件的登记、启用、执行与事件投递，以及状态变化与进程退出时向插件发布事件。
/// </remarks>
public sealed partial class DaemonHost
{
    /// <summary>
    /// 序列化插件事件负载的 JSON 选项：Web 默认值（属性名为 camelCase）。
    /// </summary>
    private static readonly JsonSerializerOptions PluginPayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 执行插件操作：<c>plugin-list</c>、<c>plugin-install</c>、<c>plugin-enable</c>、<c>plugin-remove</c>、
    /// <c>plugin-history</c>、<c>plugin-run</c> 与 <c>plugin-event</c>。
    /// </summary>
    /// <remarks>
    /// <c>plugin-run</c> 与 <c>plugin-event</c> 最多 4 个并发，名额用尽时立即失败。<c>plugin-run</c> 的 <c>Ok</c> 即插件命令是否成功；
    /// <c>plugin-event</c> 运行所有订阅该事件的已启用插件，全部成功才为 <c>Ok</c>，<c>Error</c> 取第一个失败的结果。
    /// 新安装的插件默认不启用。
    /// </remarks>
    /// <param name="request">请求。</param>
    /// <param name="token">连接的取消信号（插件请求限时 305 秒）。</param>
    /// <returns>响应。</returns>
    /// <exception cref="ArgumentException">缺少插件目录、插件标识、命令名、事件名或启用状态。</exception>
    private async Task<DaemonResponse> ExecutePluginAsync(DaemonRequest request, CancellationToken token)
    {
        switch (request.Operation)
        {
            case "plugin-list": return new(true, Plugins: plugins.Catalog.List().ToArray());
            case "plugin-install": return new(true, Plugin: plugins.Catalog.Install(
                AbsolutePath(request.Path, "Local plugin directory") ?? throw new ArgumentException("Local plugin directory is required"), request.Enabled ?? false));
            case "plugin-enable": return new(true, Plugin: plugins.Catalog.SetEnabled(RequiredId(request.PluginId),
                request.Enabled ?? throw new ArgumentException("Enabled state is required")));
            case "plugin-remove": return plugins.Catalog.Remove(RequiredId(request.PluginId))
                    ? new(true) : new(false, "Plugin not installed");
            case "plugin-history": return new(true, PluginResults: plugins.History());
            case "plugin-run":
            case "plugin-event":
                if (!pluginSlots.Wait(0)) return new(false, "Maximum 4 concurrent plugin requests; retry after a plugin finishes");
                try
                {
                    if (request.Operation == "plugin-run")
                    {
                        var result = await plugins.RunAsync(RequiredId(request.PluginId), RequiredId(request.PluginCommand),
                            request.Payload ?? default, token);
                        return new(result.Success, Error: result.Success ? null : PluginError(result), PluginResult: result);
                    }
                    var results = await plugins.RunEventAsync(RequiredId(request.EventName), request.Payload ?? default, token);
                    return new(results.All(result => result.Success),
                        Error: results.FirstOrDefault(result => !result.Success) is { } failed ? PluginError(failed) : null,
                        PluginResults: results);
                }
                finally { pluginSlots.Release(); }
            default: return new(false, "Unknown plugin operation: " + request.Operation);
        }
    }

    /// <summary>
    /// 把一个事件放入插件后台队列，不等待插件执行。
    /// </summary>
    /// <param name="name">事件名，例如 <c>agent.state</c>、<c>session.created</c>、<c>session.exited</c>、<c>message.received</c>。</param>
    /// <param name="sessionId">事件相关的会话标识。</param>
    /// <param name="detail">事件详情；与状态目录、会话标识一起序列化为 <c>{ stateDirectory, sessionId, detail }</c>。</param>
    private void QueuePluginEvent(string name, string sessionId, object? detail = null)
        => plugins.Queue(name, JsonSerializer.SerializeToElement(new { stateDirectory, sessionId, detail }, PluginPayloadJson));

    /// <summary>
    /// 记录一次 Agent 状态上报；版本号因此变化时发出 <c>agent.state</c> 插件事件。
    /// </summary>
    /// <param name="id">会话标识（必须已在活动存储中登记）。</param>
    /// <param name="state">新状态。</param>
    /// <param name="source">证据来源标签，例如 <c>api</c>、<c>hook</c>、<c>input</c>。</param>
    /// <param name="reason">面向用户的说明。</param>
    /// <returns>记录后的当前观测。</returns>
    private AgentActivity ReportAgent(string id, AgentActivityState state, string source, string reason)
    {
        var before = agentActivities.Get(id);
        var current = agentActivities.Report(id, state, source, reason);
        if (current.Revision != before.Revision) QueuePluginEvent("agent.state", id, current);
        return current;
    }

    /// <summary>
    /// 记录终端进程退出：每个会话的每次运行只记录一次，随后发出 <c>agent.state</c> 与 <c>session.exited</c>（含退出码）插件事件。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="code">退出码；未知时为 <see langword="null"/>。</param>
    private void RecordProcessExit(string id, int? code)
    {
        if (!reportedExits.TryAdd(id, 0)) return;
        var activity = agentActivities.MarkExited(id, code);
        QueuePluginEvent("agent.state", id, activity);
        QueuePluginEvent("session.exited", id, new { exitCode = code, activity });
    }

    /// <summary>
    /// 生成插件失败的错误消息：优先使用插件结果中的错误文本，否则说明退出码。
    /// </summary>
    /// <param name="result">失败的插件结果。</param>
    /// <returns>错误消息。</returns>
    private static string PluginError(LocalPluginResult result)
        => result.Error ?? "Plugin command failed with exit code " + result.ExitCode;
}
