using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;
using CrewTerm.Core.IO;
using CrewTerm.Core.Security;

namespace CrewTerm.App.Server;

/// <remarks>
/// 本分部文件：Agent 语义状态、提示词投递、等待、通知与会话间消息等协作类操作，以及原生会话的登记、恢复与遗忘入口。
/// </remarks>
public sealed partial class DaemonHost
{
    /// <summary>
    /// 请求分发入口：处理 Agent 相关操作，以 <c>plugin-</c> 开头的操作交给 <see cref="ExecutePluginAsync"/>，其余交给 <see cref="Execute"/>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>agent-hook</c>：Provider hook 回调（见 <see cref="ApplyProviderHook"/>）。</description></item>
    /// <item><description><c>agent-state</c>：不带会话标识时返回全部观测（按优先级降序），否则返回单个会话的观测。</description></item>
    /// <item><description><c>agent-report</c> / <c>agent-prompt</c>：显式上报状态；向会话发送一条提示词并提交。</description></item>
    /// <item><description><c>agent-wait</c>：等待会话进入目标状态；最多 8 个并发等待，名额用尽时立即失败。</description></item>
    /// <item><description><c>notifications</c> / <c>notification-read</c>、<c>message-send</c> / <c>message-list</c> / <c>message-ack</c>：
    /// 通知与会话间邮箱。<c>message-send</c> 只投递到邮箱，不写入目标终端。</description></item>
    /// <item><description><c>agent-handoff</c>：校验后先把消息存入邮箱，再作为提示词写入目标终端；写入失败时返回失败，但消息已保存。</description></item>
    /// <item><description><c>session-catalog</c> / <c>session-bind</c> / <c>session-resume</c> / <c>session-restart</c> / <c>session-forget</c>：
    /// 原生会话目录；遗忘前会话必须已经退出，同时删除其活动记录与 hook 配置文件。</description></item>
    /// </list>
    /// </remarks>
    /// <param name="request">已解析的请求。</param>
    /// <param name="token">连接的取消信号（含超时），用于 <c>agent-wait</c> 与插件请求。</param>
    /// <returns>响应；无效参数以异常抛出，由 <see cref="HandleConnectionAsync"/> 转换为失败响应。</returns>
    private async Task<DaemonResponse> ExecuteAgentAsync(DaemonRequest request, CancellationToken token)
    {
        switch (request.Operation)
        {
            case "agent-hook":
                return ApplyProviderHook(request);
            case "agent-state":
                return request.SessionId is null
                    ? new(true, Activities: agentActivities.List().OrderByDescending(item => agentActivities.Priority(item.SessionId)).ToArray())
                    : new(true, Activity: agentActivities.Get(request.SessionId));
            case "agent-report":
                if (request.State is null) throw new ArgumentException("State is required");
                return new(true, Activity: ReportAgent(RequiredId(request.SessionId), request.State.Value,
                    request.Source ?? "api", request.Reason ?? "Explicit state report"));
            case "agent-prompt":
                DeliverPrompt(RequiredId(request.SessionId), request.Text);
                return new(true, SessionId: request.SessionId, Activity: agentActivities.Get(request.SessionId!), Delivered: true);
            case "agent-wait":
                if (!waitSlots.Wait(0)) return new(false, "Maximum 8 concurrent Agent waits; retry after a wait completes");
                try
                {
                    var states = request.States ?? (request.State.HasValue ? [request.State.Value] : []);
                    return new(true, Activity: await agentActivities.WaitAsync(RequiredId(request.SessionId), states,
                        request.AfterRevision, TimeSpan.FromMilliseconds(request.TimeoutMilliseconds), token));
                }
                finally { waitSlots.Release(); }
            case "notifications":
                return new(true, Notifications: agentActivities.Notifications(request.SessionId).ToArray());
            case "notification-read":
                return agentActivities.AcknowledgeNotification(RequiredId(request.NotificationId))
                    ? new(true) : new(false, "Notification not found or already read");
            case "message-send":
                var sent = agentActivities.SendMessage(RequiredId(request.FromSessionId),
                    RequiredId(request.TargetSessionId), request.Text ?? "", request.InReplyTo);
                QueuePluginEvent("message.received", sent.ToSessionId, sent);
                return new(true, Message: sent, Delivered: false);
            case "message-list":
                return new(true, Messages: agentActivities.Messages(RequiredId(request.SessionId)).ToArray());
            case "message-ack":
                return agentActivities.AcknowledgeMessage(RequiredId(request.MessageId), RequiredId(request.SessionId))
                    ? new(true) : new(false, "Message not found or already acknowledged");
            case "agent-handoff":
                var target = RequiredId(request.TargetSessionId);
                ValidatePrompt(target, request.Text);
                var message = agentActivities.SendMessage(RequiredId(request.FromSessionId), target,
                    request.Text ?? "", request.InReplyTo);
                QueuePluginEvent("message.received", target, message);
                try
                {
                    DeliverPrompt(target, request.Text);
                    return new(true, SessionId: target, Message: message, Delivered: true);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return new(false, "Message was stored, but terminal delivery failed: " + error.Message, Message: message, Delivered: false); }
            case "session-catalog":
                return new(true, Sessions: ListSessionCatalog());
            case "session-bind":
                var bound = nativeSessions.Bind(RequiredId(request.SessionId), request.Provider ?? throw new ArgumentException("Provider is required"),
                    request.NativeSessionId, request.ResumeCommand);
                return new(true, SessionId: bound.Id, Sessions: [DescribeSession(bound.Id, sessions.GetValueOrDefault(bound.Id), bound)]);
            case "session-resume":
                return RestoreSession(request, resume: true);
            case "session-restart":
                return RestoreSession(request, resume: false);
            case "session-forget":
                var forgottenId = RequiredId(request.SessionId);
                lock (changeLock)
                {
                    if (sessions.TryGetValue(forgottenId, out var existing) && !existing.Terminal.HasExited)
                        return new(false, "Close the running session before forgetting its record");
                    if (sessions.TryRemove(forgottenId, out var ended)) ended.Terminal.Dispose();
                    SaveTemplates();
                    if (!nativeSessions.Forget(forgottenId)) return new(false, "Saved session not found");
                    agentActivities.Forget(forgottenId);
                    DeleteProviderHookSettings(forgottenId);
                    return new(true);
                }
            default:
                return request.Operation.StartsWith("plugin-", StringComparison.Ordinal)
                    ? await ExecutePluginAsync(request, token) : Execute(request);
        }
    }

    /// <summary>
    /// 校验一条要写入终端的提示词以及目标会话的状态。
    /// </summary>
    /// <remarks>
    /// 提示词必须非空白、不超过 32768 个字符，且不含 NUL 与 ESC（防止注入终端控制序列）；目标会话必须在运行中，
    /// 并且 Agent 不能处于 Blocked 状态——发送提示词不能被用来替用户回答工具审批。
    /// </remarks>
    /// <param name="id">目标会话标识。</param>
    /// <param name="text">提示词。</param>
    /// <exception cref="ArgumentException">提示词为空白、过长或含非法字符。</exception>
    /// <exception cref="KeyNotFoundException">会话不在内存中。</exception>
    /// <exception cref="InvalidOperationException">会话进程已退出，或 Agent 正在等待用户审批。</exception>
    private void ValidatePrompt(string id, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Prompt text is required");
        if (text.Length > 32768) throw new ArgumentException("Prompt exceeds 32768 characters");
        // 除换行、回车与制表符外拒绝一切控制字符：例如 ^C/^D 会中断或退出目标 Agent，ESC 可注入按键序列。
        if (DisplayText.ContainsControl(text, allowLineBreaks: true)) throw new ArgumentException("Prompt must not contain control characters or terminal escape sequences");
        var running = Find(id);
        if (running.Terminal.HasExited) throw new InvalidOperationException("Session has exited");
        if (agentActivities.Get(id).State == AgentActivityState.Blocked)
            throw new InvalidOperationException("Agent is blocked. Review the request and respond explicitly in its terminal; prompt delivery does not approve tools.");
    }

    /// <summary>
    /// 校验后把提示词写入会话终端并提交：去掉末尾的回车换行，再追加一个 <c>\r</c>，同时把状态推断为 Working（来源 <c>prompt</c>）。
    /// </summary>
    /// <remarks>
    /// 多行提示词只有在目标终端启用了 bracketed paste 时才投递，并作为一次粘贴整体送入后再提交一次；
    /// 否则每个换行都会被当作一次 Enter，提示词会被拆成多次提交，甚至抢先回答随后出现的审批提示。
    /// </remarks>
    /// <param name="id">目标会话标识。</param>
    /// <param name="text">提示词。</param>
    /// <exception cref="ArgumentException">提示词不合法。</exception>
    /// <exception cref="KeyNotFoundException">会话不在内存中。</exception>
    /// <exception cref="InvalidOperationException">会话已退出、Agent 正在等待审批，或多行提示词的目标未启用 bracketed paste。</exception>
    private void DeliverPrompt(string id, string? text)
    {
        ValidatePrompt(id, text);
        var body = text!.TrimEnd('\r', '\n');
        if (TerminalPaste.HasLineBreak(body))
        {
            if (!Find(id).Terminal.Screen.Snapshot().BracketedPaste)
                throw new InvalidOperationException("Multi-line prompts require the target to enable bracketed paste; send one line or wait until the Agent is ready.");
            body = TerminalPaste.Bracketed(body);
        }
        WriteSubmittedInput(id, body + "\r", "prompt", "An explicit prompt was submitted; Working is inferred until a provider event arrives");
    }

    /// <summary>
    /// 先把会话状态记为 Working，再把已提交的输入写入终端。
    /// </summary>
    /// <remarks>
    /// 先改状态再写入：若顺序相反，Provider 很快发出的 hook 事件可能先被记录，随后又被推断出的 Working 覆盖。
    /// 写入失败时，若期间没有其他状态变化（版本号仍是本次上报的），
    /// 就把状态恢复为写入前的观测，然后重新抛出异常。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="text">要写入的完整文本（已包含提交用的回车）。</param>
    /// <param name="source">状态来源标签，例如 <c>input</c> 或 <c>prompt</c>。</param>
    /// <param name="reason">面向用户的状态说明。</param>
    private void WriteSubmittedInput(string id, string text, string source, string reason)
    {
        var before = agentActivities.Get(id);
        var submitted = ReportAgent(id, AgentActivityState.Working, source, reason);
        try { Find(id).Terminal.Write(text); }
        catch
        {
            if (agentActivities.Get(id).Revision == submitted.Revision)
                ReportAgent(id, before.State, before.Source, before.Reason);
            throw;
        }
    }

    /// <summary>
    /// 把终端输出交给活动存储识别审批提示；状态因此变化时发出 <c>agent.state</c> 插件事件。
    /// </summary>
    /// <remarks>
    /// 在 ConPTY 输出读取线程上调用，因此任何异常（内存不足除外）只写入错误日志，绝不传出。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="output">新到达的一段输出（或会话启动时的初始屏幕文本）。</param>
    private void ObserveAgentOutput(string id, string output)
    {
        try
        {
            var before = agentActivities.Get(id);
            agentActivities.ObserveOutput(id, output);
            var current = agentActivities.Get(id);
            if (current.Revision != before.Revision) QueuePluginEvent("agent.state", id, current);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { LogAgentError(error); }
    }

    /// <summary>
    /// 每 100 毫秒检查一次所有会话，记录新退出的进程（每个会话只记录一次，见 <see cref="RecordProcessExit"/>）。
    /// </summary>
    /// <remarks>
    /// 单个会话检查失败只写入错误日志，不影响其他会话与后续检查。
    /// </remarks>
    /// <param name="token">daemon 的关闭信号。</param>
    /// <returns>收到关闭信号后正常完成的任务。</returns>
    private async Task MonitorActivityAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(token))
                foreach (var pair in sessions)
                {
                    try
                    {
                        if (pair.Value.Terminal.HasExited)
                            lock (changeLock)
                                // 会话可能刚被恢复为同一标识下的新进程（RestoreSession 同样持有 changeLock）：
                                // 只记录仍是当前对象的会话的退出，否则会把新会话误标为已退出并吞掉它真正的退出事件。
                                if (sessions.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                                    RecordProcessExit(pair.Key, pair.Value.Terminal.ExitCode);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { LogAgentError(error); }
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>
    /// 把 Agent 活动相关的错误追加到 <see cref="StateFiles.ServerErrorLog"/>（UTC ISO 8601 时间戳 + 异常全文）；写日志本身的 I/O 失败被忽略。
    /// </summary>
    /// <param name="error">要记录的异常。</param>
    private void LogAgentError(Exception error)
    {
        try { File.AppendAllText(StateFiles.PathOf(stateDirectory, StateFiles.ServerErrorLog), $"{DateTimeOffset.UtcNow:O} Agent activity: {error}\n"); }
        catch (IOException) { }
    }

    /// <summary>
    /// 删除为该会话生成的 Provider hook 配置文件；会话记录被遗忘后它不会再被使用。
    /// </summary>
    /// <param name="id">会话标识。</param>
    private void DeleteProviderHookSettings(string id)
    {
        try
        {
            var path = Path.Combine(StateFiles.PathOf(stateDirectory, StateFiles.ProviderHooks), id + ".json");
            if (Path.GetFileName(path) == id + ".json" && File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { LogAgentError(error); }
    }

    /// <summary>
    /// 要求请求显式给出目标标识（会话、通知、消息或插件）。
    /// </summary>
    /// <param name="id">请求中的标识。</param>
    /// <returns>原样返回的标识。</returns>
    /// <exception cref="ArgumentException">标识为 <see langword="null"/> 或空白。</exception>
    private static string RequiredId(string? id)
        => string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("An explicit target ID is required") : id;
}
