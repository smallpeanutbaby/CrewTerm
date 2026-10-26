using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Hooks;
using CrewTerm.Automation.Providers;
using CrewTerm.Core.IO;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.App.Server;

/// <remarks>
/// 本分部文件：原生会话目录的加载与迁移、按记录启动/恢复/重新启动终端会话，以及会话描述的生成。
/// </remarks>
public sealed partial class DaemonHost
{
    /// <summary>
    /// daemon 启动时加载原生会话目录：必要时从旧版 <see cref="StateFiles.SavedSessions"/> 迁移，
    /// 然后把每条记录登记到活动存储并标记为 Exited（休眠，没有终端进程）。
    /// </summary>
    /// <exception cref="InvalidDataException">旧版会话文件已损坏。</exception>
    private void LoadNativeCatalog()
    {
        var legacyPath = StateFiles.PathOf(stateDirectory, StateFiles.SavedSessions);
        // 原生目录文件一旦存在就是权威数据，即使它是被刻意清空的空目录，也不再从旧文件迁移。
        if (!File.Exists(StateFiles.PathOf(stateDirectory, StateFiles.NativeSessions)) && File.Exists(legacyPath))
            foreach (var previous in AtomicJsonStore.Load<SessionInfo[]>(legacyPath) ?? [])
                if (nativeSessions.Get(previous.Id) is null)
                    nativeSessions.Upsert(new(previous.Id, previous.Name, previous.Command, previous.WorkingDirectory,
                        previous.Provider, previous.NativeSessionId, UpdatedAt: previous.StartedAt));
        foreach (var saved in nativeSessions.List())
        {
            agentActivities.Register(saved.Id);
            agentActivities.Report(saved.Id, AgentActivityState.Exited, "process", "Saved session is dormant; no terminal process is attached");
        }
    }

    /// <summary>
    /// 为一条持久化记录重新启动终端：原生恢复原对话（<c>session-resume</c>），或以原始命令开启新对话（<c>session-restart</c>）。
    /// </summary>
    /// <remarks>
    /// 会话仍在运行时，恢复直接返回成功（“已在运行”），重启则返回失败。内存中已退出的旧会话先被释放；
    /// 新进程沿用原会话标识，记录更新为新的 Provider、原生标识与恢复命令。整个过程在 <see cref="changeLock"/> 内完成。
    /// </remarks>
    /// <param name="request">请求，需要会话标识与终端尺寸。</param>
    /// <param name="resume"><see langword="true"/> 原生恢复，<see langword="false"/> 开启新对话。</param>
    /// <returns>带会话标识的响应。</returns>
    /// <exception cref="ArgumentException">缺少会话标识，或记录中的命令/原生标识无效。</exception>
    /// <exception cref="ArgumentOutOfRangeException">终端尺寸超出范围。</exception>
    /// <exception cref="KeyNotFoundException">没有该会话的持久化记录。</exception>
    /// <exception cref="InvalidOperationException">记录无法原生恢复，或会话数已达 32 个上限。</exception>
    private DaemonResponse RestoreSession(DaemonRequest request, bool resume)
    {
        var id = RequiredId(request.SessionId);
        ValidateSize(request.Columns, request.Rows);
        lock (changeLock)
        {
            if (sessions.TryGetValue(id, out var running) && !running.Terminal.HasExited)
                return resume ? new(true, SessionId: id, Text: "Session is already running")
                    : new(false, "Close the running session before restarting it");
            var saved = nativeSessions.Get(id) ?? throw new KeyNotFoundException("Saved session not found");
            var plan = resume ? providers.PrepareResume(saved) : providers.PrepareRestart(saved);
            if (sessions.TryRemove(id, out var previous)) previous.Terminal.Dispose();
            if (sessions.Count >= 32) throw new InvalidOperationException("Maximum 32 sessions; close unused sessions first");
            var updated = saved with { Provider = plan.Provider, NativeSessionId = plan.NativeSessionId,
                ResumeCommand = plan.ResumeCommand, UpdatedAt = DateTimeOffset.UtcNow };
            StartRecordedSession(updated, plan, request.Columns, request.Rows);
            return new(true, SessionId: id, Text: resume ? "Resumed the recorded native conversation" : "Restarted as a new conversation");
        }
    }

    /// <summary>
    /// 按启动计划为一条会话记录启动 ConPTY 终端，登记活动状态并持久化记录。
    /// </summary>
    /// <remarks>
    /// <para>先为 Provider 准备 hook（可能改写命令），并通过环境变量 <c>CREWTERM_SESSION_ID</c>、<c>CREWTERM_STATE_DIR</c>、
    /// <c>CREWTERM_EXE</c>（与程序集同目录的 <c>crewterm.exe</c>）、<c>CREWTERM_PROVIDER</c> 告诉子进程如何回调。
    /// 启动后状态记为 Unknown，订阅输出以识别审批提示，并对初始屏幕做一次识别。</para>
    /// <para>进程启动之后任何一步失败都会回滚：移除并结束终端，把原生记录与活动状态恢复为调用前的值（回滚失败只写日志），
    /// 然后重新抛出原异常。全部成功后发出 <c>session.created</c> 插件事件。调用方必须持有 <see cref="changeLock"/>。</para>
    /// </remarks>
    /// <param name="record">要启动并保存的会话记录（标识即 CrewTerm 会话标识）。</param>
    /// <param name="plan">Provider 启动计划，提供实际命令与 Provider 标识。</param>
    /// <param name="columns">初始列数。</param>
    /// <param name="rows">初始行数。</param>
    private void StartRecordedSession(NativeSessionRecord record, ProviderLaunchPlan plan, int columns, int rows)
    {
        var previousRecord = nativeSessions.Get(record.Id);
        var previousActivity = agentActivities.List().FirstOrDefault(item => item.SessionId == record.Id);
        var nativeSaved = false;
        var executable = Path.Combine(Path.GetDirectoryName(typeof(DaemonHost).Assembly.Location)!, "crewterm.exe");
        var hooks = AgentHookLaunchService.Prepare(stateDirectory, record.Id, executable, plan.Command, plan.Provider);
        var environment = new Dictionary<string, string?>
        {
            ["CREWTERM_SESSION_ID"] = record.Id,
            ["CREWTERM_STATE_DIR"] = stateDirectory,
            ["CREWTERM_EXE"] = executable,
            ["CREWTERM_PROVIDER"] = plan.Provider
        };
        var terminal = ConPtySession.Start(record.Id, hooks.Command, record.WorkingDirectory, columns, rows, environment);
        try
        {
            agentActivities.Register(record.Id);
            reportedExits.TryRemove(record.Id, out _);
            agentActivities.Report(record.Id, AgentActivityState.Unknown, "process", "Terminal started; " + hooks.Reason + "; waiting for explicit Agent activity");
            terminal.OutputReceived += output => ObserveAgentOutput(record.Id, output);
            sessions[record.Id] = new(terminal, record.Name, DateTimeOffset.UtcNow);
            ObserveAgentOutput(record.Id, terminal.Screen.GetText());
            nativeSessions.Upsert(record);
            nativeSaved = true;
            SaveTemplates();
        }
        catch
        {
            sessions.TryRemove(record.Id, out _);
            terminal.Dispose();
            reportedExits.TryRemove(record.Id, out _);
            try
            {
                if (nativeSaved)
                {
                    if (previousRecord is null) nativeSessions.Forget(record.Id);
                    else nativeSessions.Upsert(previousRecord);
                }
                if (previousActivity is null) agentActivities.Forget(record.Id);
                else agentActivities.Report(record.Id, previousActivity.State, previousActivity.Source, previousActivity.Reason);
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException) { LogAgentError(rollbackError); }
            throw;
        }
        QueuePluginEvent("session.created", record.Id, record);
    }

    /// <summary>
    /// 描述原生目录中的全部会话（运行中的与休眠的），按 Agent 优先级降序、启动时间升序排列。
    /// </summary>
    /// <returns>会话描述数组；没有持久化记录的内存会话不包含在内。</returns>
    private SessionInfo[] ListSessionCatalog() => nativeSessions.List()
        .Select(record => DescribeSession(record.Id, sessions.GetValueOrDefault(record.Id), record))
        .OrderByDescending(item => item.Priority).ThenBy(item => item.StartedAt).ToArray();

    /// <summary>
    /// 合并运行时信息与持久化记录，生成一个会话的描述。
    /// </summary>
    /// <remarks>
    /// 名称、工作目录与启动时间优先取运行中的会话；命令优先取记录（未经 hook 改写的原始命令）。
    /// 没有运行中的进程时视为已退出且休眠；没有记录时不可恢复。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="running">内存中的会话；休眠时为 <see langword="null"/>。</param>
    /// <param name="record">持久化记录；尚未持久化时为 <see langword="null"/>。两者至少有一个非空。</param>
    /// <returns>会话描述，含 Agent 观测、未读数、优先级与恢复能力。</returns>
    private SessionInfo DescribeSession(string id, DaemonSession? running, NativeSessionRecord? record)
    {
        var capability = record is null ? new ProviderResumeCapability(false, "Session metadata has not been persisted")
            : providers.GetResumeCapability(record);
        return new(id, running?.Name ?? record!.Name, record?.Command ?? running!.Terminal.CommandLine,
            running?.Terminal.WorkingDirectory ?? record!.WorkingDirectory, running?.StartedAt ?? record!.UpdatedAt ?? DateTimeOffset.MinValue,
            running?.Terminal.HasExited ?? true, running?.Terminal.ExitCode, agentActivities.Get(id),
            agentActivities.UnreadCount(id), agentActivities.Priority(id), record?.Provider, record?.NativeSessionId,
            capability.CanResume, capability.Reason, IsDormant: running is null);
    }
}
