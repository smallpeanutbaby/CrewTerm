using CrewTerm.App.Protocol;
using CrewTerm.Automation.Pipelines;
using CrewTerm.Automation.Tasks;

namespace CrewTerm.App.Server;

/// <summary>
/// 在 daemon 中托管后台命令与流水线的生命周期，与是否有 UI 客户端连接无关。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>提交后立即返回任务标识；任务在线程池中排队，最多同时运行 <c>maximumConcurrency</c> 个。</description></item>
/// <item><description>排队中的任务可以被取消且不会产生任何副作用；<see cref="StopAsync"/> 会取消全部任务并等待结束。</description></item>
/// <item><description>完成的任务写入 <see cref="TaskHistory"/>，daemon 重启后仍可查询；内存中只保留最近
/// <c>retainedTasks</c> 个已完成任务，避免长期运行的 daemon 内存无限增长。</description></item>
/// </list>
/// 所有可变状态都由 <c>gate</c> 保护。
/// </remarks>
public sealed class DaemonTaskService : IDisposable
{
    /// <summary>
    /// 每个任务保留的输出字符上限（与 CLI 文档一致）。
    /// </summary>
    internal const int OutputLimit = 262144;

    /// <summary>
    /// 保护 <see cref="entries"/>、每个条目的快照与输出缓冲，以及 <see cref="stopping"/>、<see cref="disposed"/>、<see cref="stopTask"/> 的锁。
    /// </summary>
    private readonly object gate = new();

    /// <summary>
    /// 内存中的任务（排队中、运行中与最近完成的），按任务标识（32 位十六进制 GUID）序数索引；
    /// 构造时先用历史文件中的已完成任务填充。
    /// </summary>
    private readonly Dictionary<string, DaemonTaskEntry> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// 执行命令进程的运行器：并发槽数量与 <see cref="admission"/> 相同，每个任务的输出上限为 <see cref="OutputLimit"/> 个字符。
    /// </summary>
    private readonly TaskRunner runner;

    /// <summary>
    /// 已完成任务的持久化历史（<c>task-history.json</c>），保留最近 <see cref="retainedTasks"/> 条，daemon 重启后仍可查询。
    /// </summary>
    private readonly TaskHistory history;

    /// <summary>
    /// 准入信号量，与 <see cref="runner"/> 内部的并发槽数量相同。
    /// </summary>
    /// <remarks>
    /// 在任务真正开始执行之前占住名额，使仍在排队的任务可以被干净地取消（尚未启动任何进程，没有副作用）。
    /// </remarks>
    private readonly SemaphoreSlim admission;

    /// <summary>
    /// 历史文件与内存中保留的已完成任务数。
    /// </summary>
    private readonly int retainedTasks;

    /// <summary>
    /// 是否已调用 <see cref="StopAsync"/>：此后不再接受新任务，拿到名额的排队任务也不会再启动。
    /// </summary>
    private bool stopping;

    /// <summary>
    /// 是否已调用 <see cref="Dispose"/>。
    /// </summary>
    private bool disposed;

    /// <summary>
    /// 第一次 <see cref="StopAsync"/> 创建的等待任务；之后的调用都返回同一个任务。
    /// </summary>
    private Task? stopTask;

    /// <summary>
    /// 创建任务服务，并从历史文件恢复已完成任务。
    /// </summary>
    /// <param name="stateDirectory">状态目录。</param>
    /// <param name="maximumConcurrency">同时运行的任务上限。</param>
    /// <param name="retainedTasks">历史文件与内存中保留的已完成任务数。</param>
    public DaemonTaskService(string stateDirectory, int maximumConcurrency = 4, int retainedTasks = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedTasks);
        this.retainedTasks = retainedTasks;
        runner = new TaskRunner(maximumConcurrency, OutputLimit);
        admission = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        history = new TaskHistory(stateDirectory, retainedTasks);
        foreach (var result in history.List())
        {
            var error = result.ExitCode is null && !result.Cancelled ? "Task failed before reporting an exit code; see output." : null;
            entries[result.Id] = new(new(result.Id, result.Name, result.Command, result.WorkingDirectory,
                result.StartedAt, true, result.ExitCode, result.Cancelled, Tail(result.Output), error), null);
        }
    }

    /// <summary>
    /// 提交一条后台命令。
    /// </summary>
    /// <param name="command">要运行的命令。</param>
    /// <returns>任务标识。</returns>
    /// <exception cref="ArgumentException">名称、命令或工作目录为空白。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    /// <exception cref="InvalidOperationException">服务正在停止。</exception>
    /// <exception cref="ObjectDisposedException">服务已释放。</exception>
    public string Start(CommandTask command)
    {
        command = ValidateCommand(command);
        return StartCore(command.Name, command.Command, command.WorkingDirectory,
            entry => runner.RunAsync(command, text => AppendOutput(entry, text), entry.Cancellation!.Token));
    }

    /// <summary>
    /// 提交一条顺序流水线：任一步失败或取消即停止，整体作为一个任务显示。
    /// </summary>
    /// <param name="name">流水线名称。</param>
    /// <param name="commands">按顺序执行的步骤，至少一个。</param>
    /// <returns>任务标识。</returns>
    /// <exception cref="ArgumentException">名称为空白、没有步骤，或某个步骤的名称、命令、工作目录为空白。</exception>
    /// <exception cref="DirectoryNotFoundException">某个步骤的工作目录不存在。</exception>
    /// <exception cref="InvalidOperationException">服务正在停止。</exception>
    public string StartPipeline(string name, IReadOnlyList<CommandTask> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) throw new ArgumentException("A pipeline must contain at least one command.", nameof(commands));
        var steps = commands.Select(ValidateCommand).ToArray();
        var description = string.Join(Environment.NewLine, steps.Select(step => $"{step.Name}: {step.Command}"));
        return StartCore(name, description, steps[0].WorkingDirectory, async entry =>
        {
            var result = await new PipelineRunner(runner).RunAsync(steps,
                text => AppendOutput(entry, text), entry.Cancellation!.Token).ConfigureAwait(false);
            var last = result.Steps.LastOrDefault();
            string output;
            lock (gate) output = entry.Output.ToString();
            // 流水线的退出码取最后执行的步骤：成功时为 0，失败时即失败步骤的退出码。
            return new(entry.Info.Id, name, description, steps[0].WorkingDirectory,
                result.Steps.FirstOrDefault()?.StartedAt ?? entry.Info.StartedAt,
                last?.FinishedAt ?? DateTimeOffset.UtcNow, last?.ExitCode, output, result.Cancelled,
                result.Steps.Any(step => step.OutputTruncated));
        });
    }

    /// <summary>
    /// 列出内存中的全部任务（运行中与最近完成的），最新提交的在前。
    /// </summary>
    /// <returns>任务快照数组。</returns>
    public DaemonTaskInfo[] List()
    {
        lock (gate) return entries.Values.Select(Snapshot)
            .OrderByDescending(info => info.StartedAt).ToArray();
    }

    /// <summary>
    /// 获取单个任务的快照（含当前输出）。
    /// </summary>
    /// <param name="id">任务标识。</param>
    /// <returns>任务快照；不存在时为 <see langword="null"/>。</returns>
    public DaemonTaskInfo? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate) return entries.TryGetValue(id, out var entry) ? Snapshot(entry) : null;
    }

    /// <summary>
    /// 请求取消运行中或排队中的任务；结果需要再次查询确认。
    /// </summary>
    /// <param name="id">任务标识。</param>
    /// <returns>已发出取消请求时为 <see langword="true"/>；任务不存在或已结束时为 <see langword="false"/>。</returns>
    public bool Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(id, out var entry) || entry.Info.Completed || entry.Cancellation is null)
                return false;
            _ = entry.Cancellation.CancelAsync();
            return true;
        }
    }

    /// <summary>
    /// 停止接受新任务，取消全部运行中与排队中的任务，并等待它们记录最终结果。可重复调用。
    /// </summary>
    /// <returns>所有任务结束时完成的任务。</returns>
    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null) return stopTask;
            stopping = true;
            var active = entries.Values.Where(entry => !entry.Info.Completed).ToArray();
            stopTask = StopJobsAsync(active);
            return stopTask;
        }
    }

    /// <summary>
    /// 取消给定任务并等待它们全部完成。
    /// </summary>
    /// <param name="active">停止时仍未结束的任务。</param>
    /// <returns>表示等待过程的任务。</returns>
    private static async Task StopJobsAsync(DaemonTaskEntry[] active)
    {
        var cancellations = active.Where(entry => entry.Cancellation is not null)
            .Select(entry => entry.Cancellation!.CancelAsync()).ToArray();
        await Task.WhenAll(cancellations).ConfigureAwait(false);
        await Task.WhenAll(active.Select(entry => entry.Completion)).ConfigureAwait(false);
    }

    /// <summary>
    /// 登记任务并在线程池中开始执行。
    /// </summary>
    /// <param name="name">任务名称。</param>
    /// <param name="command">显示用的命令文本。</param>
    /// <param name="directory">工作目录。</param>
    /// <param name="execute">实际执行逻辑。</param>
    /// <returns>任务标识。</returns>
    private string StartCore(string name, string command, string directory, Func<DaemonTaskEntry, Task<TaskRunResult>> execute)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping) throw new InvalidOperationException("The task service is stopping.");
            var id = Guid.NewGuid().ToString("N");
            var info = new DaemonTaskInfo(id, name, command, directory, DateTimeOffset.UtcNow, false, null, false, "", null);
            var entry = new DaemonTaskEntry(info, new CancellationTokenSource());
            entries.Add(id, entry);
            entry.Completion = Task.Run(() => ExecuteAsync(entry, execute));
            return id;
        }
    }

    /// <summary>
    /// 等待并发名额、执行任务、写入历史，并发布最终快照。任何异常都转换为已完成的失败任务。
    /// </summary>
    /// <param name="entry">任务状态。</param>
    /// <param name="execute">实际执行逻辑。</param>
    /// <returns>表示执行过程的任务。</returns>
    private async Task ExecuteAsync(DaemonTaskEntry entry, Func<DaemonTaskEntry, Task<TaskRunResult>> execute)
    {
        TaskRunResult result;
        string? error = null;
        var admitted = false;
        try
        {
            await admission.WaitAsync(entry.Cancellation!.Token).ConfigureAwait(false);
            admitted = true;
            // 拿到名额时服务可能已开始停止：此时不再启动进程，保证排队任务没有副作用。
            lock (gate)
                if (stopping) throw new OperationCanceledException("The task service is stopping.");
            result = await execute(entry).ConfigureAwait(false);
            result = result with { Id = entry.Info.Id };
        }
        catch (OperationCanceledException)
        {
            result = FailedResult(entry, cancelled: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.Message;
            AppendOutput(entry, Environment.NewLine + "[CrewTerm] " + error + Environment.NewLine);
            result = FailedResult(entry, cancelled: false);
        }
        finally
        {
            if (admitted) admission.Release();
        }

        try { history.Add(result); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"Task finished, but its history could not be saved: {exception.Message}";
        }
        lock (gate)
        {
            entry.Info = entry.Info with
            {
                Completed = true, ExitCode = result.ExitCode, Cancelled = result.Cancelled,
                Output = Tail(result.Output), Error = error
            };
            entry.Output.Clear();
            PruneCompleted();
        }
    }

    /// <summary>
    /// 只在内存中保留最近 <c>retainedTasks</c> 个已完成任务（与历史文件一致）。调用方必须持有 <c>gate</c>。
    /// </summary>
    private void PruneCompleted()
    {
        var expired = entries.Values.Where(entry => entry.Info.Completed)
            .OrderByDescending(entry => entry.Info.StartedAt).Skip(retainedTasks).ToArray();
        foreach (var entry in expired)
        {
            entries.Remove(entry.Info.Id);
            entry.Cancellation?.Dispose();
        }
    }

    /// <summary>
    /// 生成对外快照：运行中的任务在此时才把输出缓冲转换为字符串。调用方必须持有 <c>gate</c>。
    /// </summary>
    /// <param name="entry">任务状态。</param>
    /// <returns>任务快照。</returns>
    private static DaemonTaskInfo Snapshot(DaemonTaskEntry entry)
        => entry.Info.Completed ? entry.Info : entry.Info with { Output = entry.Output.ToString() };

    /// <summary>
    /// 为取消或异常结束的任务构造结果（没有退出码）。
    /// </summary>
    /// <param name="entry">任务状态。</param>
    /// <param name="cancelled">是否因取消而结束。</param>
    /// <returns>任务结果。</returns>
    private TaskRunResult FailedResult(DaemonTaskEntry entry, bool cancelled)
    {
        lock (gate)
            return new(entry.Info.Id, entry.Info.Name, entry.Info.Command, entry.Info.WorkingDirectory,
                entry.Info.StartedAt, DateTimeOffset.UtcNow, null, entry.Output.ToString(), cancelled);
    }

    /// <summary>
    /// 追加实时输出。只写入有界缓冲，不在每个输出块上重建快照字符串。
    /// </summary>
    /// <param name="entry">任务状态。</param>
    /// <param name="output">输出片段。</param>
    private void AppendOutput(DaemonTaskEntry entry, string output)
    {
        lock (gate) entry.Output.Append(output);
    }

    /// <summary>
    /// 校验命令并把工作目录转换为绝对路径。
    /// </summary>
    /// <param name="command">要校验的命令。</param>
    /// <returns>工作目录已规范化的命令。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">名称、命令或工作目录为空白。</exception>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    private static CommandTask ValidateCommand(CommandTask command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkingDirectory);
        var directory = Path.GetFullPath(command.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        return command with { WorkingDirectory = directory };
    }

    /// <summary>
    /// 截取文本末尾的至多 <see cref="OutputLimit"/> 个字符。
    /// </summary>
    /// <param name="text">完整文本。</param>
    /// <returns>末尾文本。</returns>
    private static string Tail(string text) => text.Length <= OutputLimit ? text : text[^OutputLimit..];

    /// <summary>
    /// 停止全部任务并释放资源。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        StopAsync().GetAwaiter().GetResult();
        lock (gate)
        {
            foreach (var entry in entries.Values) entry.Cancellation?.Dispose();
            admission.Dispose();
        }
    }
}
