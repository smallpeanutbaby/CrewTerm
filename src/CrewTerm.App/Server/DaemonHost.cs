using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;
using CrewTerm.Automation.Providers;
using CrewTerm.Automation.Tasks;
using CrewTerm.Core.IO;
using CrewTerm.Core.Security;
using CrewTerm.Core.Settings;

namespace CrewTerm.App.Server;

/// <summary>
/// CrewTerm daemon 的主体：持有全部终端会话（ConPTY）、后台任务、Agent 活动状态、原生会话目录与本地插件，
/// 通过命名管道响应 UI 与 CLI 客户端的请求。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>每个状态目录只运行一个 daemon：<see cref="RunAsync"/> 期间以独占方式打开 <see cref="StateFiles.ServerLock"/>。</description></item>
/// <item><description>管道只允许当前 Windows 用户连接，最多同时处理 16 个连接；每个连接只交换一条请求和一条响应（见 <see cref="PipeProtocol"/>）。</description></item>
/// <item><description>终端进程属于 daemon 而不是某个 UI 客户端：客户端退出后会话继续运行，之后可以重新连接。</description></item>
/// <item><description>请求处理是并发的：<c>sessions</c> 是并发字典，读取无需加锁；会话集合的变更与会话记录的持久化由 <c>changeLock</c> 串行化。</description></item>
/// <item><description>分部文件：<c>DaemonHost.Agents.cs</c>（Agent 状态、提示词与消息）、<c>DaemonHost.Hooks.cs</c>（Provider hook 回调）、
/// <c>DaemonHost.NativeSessions.cs</c>（原生会话目录、恢复与重启）、<c>DaemonHost.Plugins.cs</c>（本地插件）。</description></item>
/// </list>
/// </remarks>
public sealed partial class DaemonHost : IDisposable
{
    /// <summary>
    /// 状态目录的绝对路径；决定管道名、锁文件以及所有持久化文件的位置。
    /// </summary>
    private readonly string stateDirectory;

    /// <summary>
    /// 正在运行或已退出但尚未关闭的终端会话，按 CrewTerm 会话标识索引。
    /// </summary>
    /// <remarks>
    /// 并发字典，查询无需加锁；创建、恢复、遗忘等需要“检查上限 + 修改 + 持久化”的操作在 <see cref="changeLock"/> 内完成。
    /// </remarks>
    private readonly ConcurrentDictionary<string, DaemonSession> sessions = new();

    /// <summary>
    /// 关闭信号：成功响应 <c>stop</c> 请求或调用 <see cref="Dispose"/> 时取消，使 <see cref="RunAsync"/> 退出监听循环。
    /// </summary>
    private readonly CancellationTokenSource shutdown = new();

    /// <summary>
    /// 串行化会话集合的变更（创建、关闭后的持久化、恢复、重启、遗忘、hook 重新绑定）以及 <see cref="SaveTemplates"/> 的写入。
    /// </summary>
    private readonly object changeLock = new();

    /// <summary>
    /// 同时处理的连接数上限（16，与管道实例上限一致）。监听循环先取得名额再创建管道实例，连接处理结束后归还。
    /// </summary>
    private readonly SemaphoreSlim connectionSlots = new(16, 16);

    /// <summary>
    /// 后台命令任务与流水线的调度、执行与历史记录。
    /// </summary>
    private readonly DaemonTaskService taskService;

    /// <summary>
    /// Agent 语义状态、通知与会话间消息的持久化存储；daemon 是它唯一的写入方。
    /// </summary>
    private readonly AgentActivityStore agentActivities;

    /// <summary>
    /// 终端会话的持久化目录（<see cref="StateFiles.NativeSessions"/>）：启动命令、Provider 与原生对话标识，
    /// 进程退出和 daemon 重启后仍然保留，用于恢复或重新启动。
    /// </summary>
    private readonly NativeSessionStore nativeSessions;

    /// <summary>
    /// 按 Provider（claude、codex 等）准备启动、恢复与重新启动命令，并判断记录能否原生恢复。
    /// </summary>
    private readonly ProviderSessionService providers = new();

    /// <summary>
    /// 同时进行的 <c>agent-wait</c> 请求上限（8）。名额用尽时立即拒绝而不是排队，避免长时间等待占满连接。
    /// </summary>
    private readonly SemaphoreSlim waitSlots = new(8, 8);

    /// <summary>
    /// 同时进行的 <c>plugin-run</c> / <c>plugin-event</c> 请求上限（4）。名额用尽时立即拒绝。
    /// </summary>
    private readonly SemaphoreSlim pluginSlots = new(4, 4);

    /// <summary>
    /// 本地插件目录、按需执行与后台事件队列。
    /// </summary>
    private readonly DaemonPluginService plugins;

    /// <summary>
    /// 已记录过进程退出的会话标识（值没有意义），保证每次进程退出只记录一次 <c>Exited</c> 状态并只发出一次 <c>session.exited</c> 事件；
    /// 会话重新启动时移除。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> reportedExits = new();

    /// <summary>
    /// 是否已调用 <see cref="Dispose"/>。
    /// </summary>
    private bool disposed;

    /// <summary>
    /// <see cref="RunAsync"/> 是否已取得锁文件并开始运行。开始后三个信号量改由 <see cref="RunAsync"/> 在所有连接处理结束后释放。
    /// </summary>
    private bool runStarted;

    /// <summary>
    /// 准备 daemon：创建状态目录（若不存在），读取工作区设置以确定后台任务并发上限，并打开各持久化存储。不会开始监听。
    /// </summary>
    /// <param name="stateDirectory">状态目录，可以是相对路径。</param>
    /// <exception cref="InvalidDataException">工作区设置或某个状态文件已损坏（原文件保持不变）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">工作区设置中的并发上限超出 1–32。</exception>
    public DaemonHost(string stateDirectory)
    {
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        StateFiles.EnsureDirectory(this.stateDirectory);
        var settings = new WorkspaceSettingsStore(this.stateDirectory).Load();
        taskService = new DaemonTaskService(this.stateDirectory, settings.MaximumConcurrentTasks);
        agentActivities = new AgentActivityStore(this.stateDirectory);
        nativeSessions = new NativeSessionStore(this.stateDirectory);
        plugins = new DaemonPluginService(this.stateDirectory);
    }

    /// <summary>
    /// 运行 daemon，直到成功响应 <c>stop</c> 请求、调用 <see cref="Dispose"/> 或 <paramref name="token"/> 被取消。
    /// </summary>
    /// <remarks>
    /// <para>启动时独占打开锁文件，迁移并加载原生会话目录，启动进程退出监视；随后循环接受管道连接，每个连接在独立任务中处理。</para>
    /// <para>退出时先取消所有连接处理并等待它们结束，再依次等待退出监视、停止插件服务与后台任务服务，最后释放信号量。
    /// 终端会话不会在这里结束，由 <see cref="Dispose"/> 负责。每个实例只能运行一次。</para>
    /// </remarks>
    /// <param name="token">外部取消信号。</param>
    /// <returns>daemon 完全停止后完成的任务；正常关闭不抛出 <see cref="OperationCanceledException"/>。</returns>
    /// <exception cref="ObjectDisposedException">实例已释放。</exception>
    /// <exception cref="IOException">锁文件被占用，即同一状态目录已有 daemon 在运行。</exception>
    public async Task RunAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        using var processLock = new FileStream(StateFiles.PathOf(stateDirectory, StateFiles.ServerLock), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        runStarted = true;
        var handlers = new List<Task>();
        var activityMonitor = Task.CompletedTask;
        try
        {
            LoadNativeCatalog();
            activityMonitor = MonitorActivityAsync(linked.Token);
            while (!linked.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(linked.Token);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeProtocol.PipeName(stateDirectory), PipeDirection.InOut,
                        16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(linked.Token);
                }
                catch { pipe?.Dispose(); connectionSlots.Release(); throw; }
                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(HandleConnectionSlotAsync(pipe, linked.Token));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            await linked.CancelAsync();
            try { await Task.WhenAll(handlers); }
            finally
            {
                try { await activityMonitor; await plugins.StopAsync(); await taskService.StopAsync(); }
                finally { connectionSlots.Dispose(); waitSlots.Dispose(); pluginSlots.Dispose(); shutdown.Dispose(); }
            }
        }
    }

    /// <summary>
    /// 处理一个连接，结束后（无论成败）归还 <see cref="connectionSlots"/> 中的名额。
    /// </summary>
    /// <param name="pipe">已建立连接的管道实例，所有权转移给本方法。</param>
    /// <param name="token">daemon 的关闭信号。</param>
    /// <returns>连接处理结束后完成的任务。</returns>
    private async Task HandleConnectionSlotAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try { await HandleConnectionAsync(pipe, token); }
        finally { connectionSlots.Release(); }
    }

    /// <summary>
    /// 在一个连接上读取一行 JSON 请求、执行并写回一行 JSON 响应，然后关闭管道。
    /// </summary>
    /// <remarks>
    /// <para>整个连接默认限时 15 秒；<c>agent-wait</c> 延长为其等待超时（限制在 1–60000 毫秒）再加 2 秒，
    /// 插件请求延长为 305 秒。客户端（<c>DaemonClient</c>）对这两类请求的超时分别多留 1 秒和 5 秒，保证客户端能收到 daemon 的失败响应。</para>
    /// <para>请求解析或执行中抛出的异常（内存不足除外）都转换为 <c>Ok = false</c> 的响应，消息即异常消息；
    /// 连接本身的 I/O 错误、超时与 JSON 错误被吞掉（客户端已经离开）。成功响应 <c>stop</c> 之后触发关闭信号。</para>
    /// </remarks>
    /// <param name="pipe">已建立连接的管道实例，本方法结束时释放。</param>
    /// <param name="token">daemon 的关闭信号。</param>
    /// <returns>连接关闭后完成的任务。</returns>
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await PipeProtocol.ReadLineAsync(reader, PipeProtocol.MaxRequest, timeout.Token);
                DaemonResponse response;
                DaemonRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<DaemonRequest>(line, PipeProtocol.Json);
                    if (request?.Operation == "agent-wait")
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds, 1, 60000) + 2000));
                    if (request?.Operation is "plugin-run" or "plugin-event") timeout.CancelAfter(TimeSpan.FromSeconds(305));
                    response = request is null ? new(false, "Empty request") : await ExecuteAgentAsync(request, timeout.Token);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { response = new(false, error.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeProtocol.Json).AsMemory(), timeout.Token);
                if (request?.Operation == "stop" && response.Ok) await shutdown.CancelAsync();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        }
    }

    /// <summary>
    /// 执行基础操作：<c>ping</c>、<c>list</c>、<c>stop</c>、后台任务（<c>task-*</c>、<c>pipeline-start</c>）
    /// 以及终端会话的 <c>create</c>、<c>close</c>、<c>screen</c>、<c>input</c>、<c>resize</c>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>create</c>：命令最长 8192 个字符，工作目录缺省为状态目录且必须存在，最多 32 个会话；在 <see cref="changeLock"/> 内准备 Provider 启动计划、启动并持久化。</description></item>
    /// <item><description><c>close</c>：结束会话的进程树，记录退出状态并重写会话列表；会话记录仍保留在原生目录中。</description></item>
    /// <item><description><c>input</c>：已知 Agent Provider（claude、codex、gemini、opencode、copilot）的会话收到以回车结束的输入时，
    /// 先把状态推断为 Working 再写入；其他输入原样写入。</description></item>
    /// <item><description><c>task-list</c> 清空每个任务的输出文本以控制响应体积，完整输出通过 <c>task-get</c> 获取。</description></item>
    /// </list>
    /// 无效参数以异常形式抛出，由 <see cref="HandleConnectionAsync"/> 转换为失败响应；未知操作直接返回失败响应。
    /// </remarks>
    /// <param name="request">已解析的请求。</param>
    /// <returns>响应；业务上的“未找到”等以 <c>Ok = false</c> 表示。</returns>
    /// <exception cref="ArgumentException">缺少会话标识、命令为空或过长、流水线没有步骤。</exception>
    /// <exception cref="ArgumentOutOfRangeException">终端尺寸超出范围。</exception>
    /// <exception cref="KeyNotFoundException">会话不存在。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    /// <exception cref="InvalidOperationException">会话数已达 32 个上限。</exception>
    private DaemonResponse Execute(DaemonRequest request)
    {
        switch (request.Operation)
        {
            case "ping": return new(true, Text: "CrewTerm " + CrewTermVersion.Current,
                Version: CrewTermVersion.Current, ProtocolVersion: CrewTermVersion.ProtocolVersion);
            case "list": return new(true, Sessions: ListSessions());
            case "stop": return new(true);
            case "task-start":
                ValidateTaskCommand(request.Command);
                ValidateDisplayText(request.Name, "Task name", 200);
                var taskId = taskService.Start(new CommandTask(
                    string.IsNullOrWhiteSpace(request.Name) ? request.Command! : request.Name,
                    request.Command!, AbsolutePath(request.WorkingDirectory, "Working directory") ?? stateDirectory));
                return new(true, SessionId: taskId);
            case "pipeline-start":
                if (request.Steps is null || request.Steps.Length == 0)
                    throw new ArgumentException("At least one pipeline step is required");
                ValidateDisplayText(request.Name, "Pipeline name", 200);
                foreach (var step in request.Steps)
                {
                    ArgumentNullException.ThrowIfNull(step);
                    ValidateTaskCommand(step.Command);
                    ValidateDisplayText(step.Name, "Pipeline step name", 200);
                    AbsolutePath(step.WorkingDirectory, "Working directory");
                }
                var pipelineId = taskService.StartPipeline(
                    string.IsNullOrWhiteSpace(request.Name) ? "Pipeline" : request.Name, request.Steps);
                return new(true, SessionId: pipelineId);
            case "task-list":
                return new(true, Tasks: taskService.List().Select(info => info with { Output = "" }).ToArray());
            case "task-get":
                var task = taskService.Get(RequiredId(request.SessionId));
                return task is null ? new(false, "Task not found") : new(true, Task: task);
            case "task-cancel":
                if (!taskService.Cancel(RequiredId(request.SessionId)))
                    return new(false, "Task not found or already completed");
                return new(true);
            case "create":
                ValidateSize(request.Columns, request.Rows);
                if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("Command is required");
                ValidateDisplayText(request.Command, "Command", 8192);
                ValidateDisplayText(request.Name, "Session name", 200);
                var cwd = FileSystemPath.Normalize(AbsolutePath(request.WorkingDirectory, "Working directory") ?? stateDirectory);
                if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
                lock (changeLock)
                {
                    if (sessions.Count >= 32) throw new InvalidOperationException("Maximum 32 sessions; close unused sessions first");
                    var id = Guid.NewGuid().ToString("N")[..12];
                    var plan = providers.PrepareLaunch(request.Command, request.Provider, request.NativeSessionId, request.ResumeCommand);
                    var record = new NativeSessionRecord(id, string.IsNullOrWhiteSpace(request.Name) ? request.Command : request.Name,
                        request.Command, cwd, plan.Provider, plan.NativeSessionId, plan.ResumeCommand, DateTimeOffset.UtcNow);
                    StartRecordedSession(record, plan, request.Columns, request.Rows);
                    return new(true, SessionId: id);
                }
            case "close":
                var closingId = RequiredId(request.SessionId);
                if (!sessions.TryRemove(closingId, out var closing)) return new(false, "Session not found");
                closing.Terminal.Dispose();
                RecordProcessExit(closingId, closing.Terminal.ExitCode);
                lock (changeLock) SaveTemplates();
                return new(true);
            case "screen":
                var screen = Find(request.SessionId).Terminal.Screen;
                return new(true, Screen: screen.Snapshot(), Text: request.IncludeText ? screen.GetText() : null);
            case "input":
                var input = Find(request.SessionId).Terminal;
                if (input.HasExited) return new(false, "Session has exited");
                var text = request.Text ?? "";
                var provider = nativeSessions.Get(request.SessionId!)?.Provider;
                if (provider is "claude" or "codex" or "gemini" or "opencode" or "copilot"
                    && (text.EndsWith('\r') || text.EndsWith("\r\n", StringComparison.Ordinal)))
                    WriteSubmittedInput(request.SessionId!, text, "input", "User submitted terminal input; Working is inferred until a provider event arrives");
                else input.Write(text);
                return new(true);
            case "resize":
                ValidateSize(request.Columns, request.Rows);
                Find(request.SessionId).Terminal.Resize(request.Columns, request.Rows);
                return new(true);
            default: return new(false, "Unknown operation: " + request.Operation);
        }
    }

    /// <summary>
    /// 查找内存中的会话（运行中，或已退出但尚未关闭）。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <returns>会话。</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> 为空白。</exception>
    /// <exception cref="KeyNotFoundException">没有该会话（可能只存在于原生目录中，处于休眠状态）。</exception>
    private DaemonSession Find(string? id) => sessions.TryGetValue(RequiredId(id), out var value)
        ? value : throw new KeyNotFoundException("Session not found");

    /// <summary>
    /// 描述内存中的全部会话（不含只存在于原生目录中的休眠会话），按 Agent 优先级降序、启动时间升序排列。
    /// </summary>
    /// <returns>会话描述数组。</returns>
    private SessionInfo[] ListSessions()
    {
        var records = nativeSessions.List().ToDictionary(item => item.Id);
        return sessions.Select(pair => DescribeSession(pair.Key, pair.Value, records.GetValueOrDefault(pair.Key)))
            .OrderByDescending(item => item.Priority).ThenBy(item => item.StartedAt).ToArray();
    }

    /// <summary>
    /// 把 <see cref="ListSessions"/> 的结果写入 <see cref="StateFiles.SavedSessions"/>。
    /// </summary>
    /// <remarks>
    /// 这是旧版本的会话记录格式，保留用于兼容；新版本以 <see cref="StateFiles.NativeSessions"/> 为准，
    /// 只有原生目录文件不存在时才会从这里迁移（见 <see cref="LoadNativeCatalog"/>）。调用方应持有 <see cref="changeLock"/>。
    /// </remarks>
    private void SaveTemplates() => AtomicJsonStore.Save(StateFiles.PathOf(stateDirectory, StateFiles.SavedSessions), ListSessions());

    /// <summary>
    /// 校验后台任务的命令：非空白且不超过 8192 个字符。
    /// </summary>
    /// <param name="command">命令行。</param>
    /// <exception cref="ArgumentException">命令为空白或过长。</exception>
    private static void ValidateTaskCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required");
        ValidateDisplayText(command, "Command", 8192);
    }

    /// <summary>
    /// 要求客户端传来的路径是绝对路径。
    /// </summary>
    /// <remarks>
    /// daemon 的当前目录是程序所在目录，相对路径若在这里解析会悄悄指向安装目录；
    /// 因此相对路径必须由客户端按它自己的当前目录转换后再发送。
    /// </remarks>
    /// <param name="path">客户端传来的路径；为 <see langword="null"/> 时表示未提供。</param>
    /// <param name="label">出错时写入异常消息的字段名。</param>
    /// <returns>原样返回 <paramref name="path"/>。</returns>
    /// <exception cref="ArgumentException">路径不是完整的绝对路径。</exception>
    internal static string? AbsolutePath(string? path, string label)
        => path is null || Path.IsPathFullyQualified(path) ? path : throw new ArgumentException(label + " must be an absolute path: " + path);

    /// <summary>
    /// 校验会显示在工作台界面上的命令、会话名或任务名：不超过长度上限，且不含任何控制字符。
    /// </summary>
    /// <remarks>
    /// 这些文本会出现在会话列表、面板标题与任务列表中；夹带 <c>ESC</c> 等控制字符时可能被宿主终端当作控制序列执行，
    /// 因此在 daemon 入口直接拒绝（见 <see cref="DisplayText"/>）。
    /// </remarks>
    /// <param name="text">要校验的文本；为 <see langword="null"/> 时不校验。</param>
    /// <param name="label">出错时写入异常消息的字段名。</param>
    /// <param name="maximum">最大字符数。</param>
    /// <exception cref="ArgumentException">超过长度上限或含有控制字符。</exception>
    private static void ValidateDisplayText(string? text, string label, int maximum)
    {
        if (text is null) return;
        if (text.Length > maximum) throw new ArgumentException(label + " is too long");
        if (DisplayText.ContainsControl(text, allowLineBreaks: false)) throw new ArgumentException(label + " must not contain control characters");
    }

    /// <summary>
    /// 校验客户端请求的终端尺寸：2–300 列、2–120 行（比 <see cref="CrewTerm.Terminal.Emulation.TerminalScreen"/> 支持的范围更严格）。
    /// </summary>
    /// <param name="columns">列数。</param>
    /// <param name="rows">行数。</param>
    /// <exception cref="ArgumentOutOfRangeException">列数或行数超出范围（异常的参数名指明是哪一个）。</exception>
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 2 or > 300) throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size must be 2–300 columns and 2–120 rows");
        if (rows is < 2 or > 120) throw new ArgumentOutOfRangeException(nameof(rows), "Terminal size must be 2–300 columns and 2–120 rows");
    }

    /// <summary>
    /// 停止 daemon 并释放资源：触发关闭信号，释放插件服务与后台任务服务，结束所有终端会话的进程树。
    /// </summary>
    /// <remarks>
    /// 重复调用无效果。若 <see cref="RunAsync"/> 从未开始，这里同时释放关闭信号与三个信号量；
    /// 否则由 <see cref="RunAsync"/> 在连接处理全部结束后释放，避免仍在运行的连接处理访问已释放的对象。
    /// </remarks>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // RunAsync 已经结束时关闭信号已被它释放，此时无需再触发。
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        plugins.Dispose();
        taskService.Dispose();
        foreach (var session in sessions.Values) session.Terminal.Dispose();
        sessions.Clear();
        // 取消展开期间，仍在运行的连接处理还会归还它们的名额、可能触发关闭信号；
        // 因此这些处理结束之后的释放工作归 RunAsync 负责。
        if (!runStarted) { shutdown.Dispose(); connectionSlots.Dispose(); waitSlots.Dispose(); pluginSlots.Dispose(); }
    }
}
