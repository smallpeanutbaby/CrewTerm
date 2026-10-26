using System.Collections.Concurrent;

namespace CrewTerm.App.UI.Input;

/// <summary>
/// 按会话串行化终端 resize 请求，等待期间只保留最新的目标尺寸。
/// </summary>
/// <remarks>
/// 拖动窗口时 UI 会连续产生大量尺寸变化。每个会话同时最多只有一个 resize 请求在途；
/// 期间到来的新尺寸只保留最后一个，在途请求完成后再发送，避免请求堆积和乱序。
/// 已确认的尺寸与请求相同时直接返回，不重复发送。
/// </remarks>
/// <param name="resize">实际执行 resize 的委托：会话标识、列数、行数。</param>
public sealed class TerminalResizeCoordinator(Func<string, int, int, Task> resize)
{
    /// <summary>
    /// 按会话标识（区分大小写）保存的 resize 状态；首次请求时创建，之后不会移除。
    /// </summary>
    private readonly ConcurrentDictionary<string, ResizeSessionState> sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// 请求把会话调整为指定尺寸；当本批请求的最新尺寸已生效或发送失败时完成。
    /// </summary>
    /// <remarks>
    /// 线程安全，可在任意线程调用，本身不会阻塞。
    /// 已有在途请求时只记录新尺寸并返回同一批次的任务；与已确认尺寸相同时返回已完成的任务；
    /// 否则开启新批次并在后台开始发送。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="columns">列数。</param>
    /// <param name="rows">行数。</param>
    /// <returns>本批请求的完成任务；失败时携带最后一次 resize 的异常。</returns>
    public Task RequestAsync(string id, int columns, int rows)
    {
        var state = sessions.GetOrAdd(id, _ => new());
        TaskCompletionSource completion;
        lock (state.Gate)
        {
            var requested = new TerminalSize(columns, rows);
            state.Latest = requested;
            if (state.Completion is not null) return state.Completion.Task;
            if (state.Confirmed == requested) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Completion = completion;
        }
        _ = ApplyLatestAsync(id, state, completion);
        return completion.Task;
    }

    /// <summary>
    /// 循环发送最新尺寸，直到发送完成时没有更新的请求为止。
    /// </summary>
    /// <remarks>
    /// 中间某次发送失败但随后又有新尺寸到来时，该失败被丢弃并继续发送新尺寸；
    /// 只有最后一次发送的结果决定批次任务是成功还是携带异常。本方法自身从不抛出异常。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    /// <param name="state">会话的 resize 状态。</param>
    /// <param name="completion">本批请求的完成源。</param>
    /// <returns>表示发送过程的任务。</returns>
    private async Task ApplyLatestAsync(string id, ResizeSessionState state, TaskCompletionSource completion)
    {
        while (true)
        {
            TerminalSize requested;
            lock (state.Gate) requested = state.Latest;

            Exception? failure = null;
            try { await resize(id, requested.Columns, requested.Rows).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }

            lock (state.Gate)
            {
                // 响应丢失时远端尺寸未知：即使之前确认过旧尺寸，也要清除确认，确保下次重新发送。
                state.Confirmed = failure is null ? requested : null;
                if (state.Latest != requested) continue;
                state.Completion = null;
                if (failure is null) completion.SetResult();
                else completion.SetException(failure);
                return;
            }
        }
    }

    /// <summary>
    /// 忘记一个会话已确认的尺寸（例如同一会话标识被恢复为新进程后），下一次请求无论尺寸是否相同都会重新发送。
    /// </summary>
    /// <remarks>
    /// 有在途请求时只清除确认状态，在途请求完成后照常继续；可以从任意线程调用。
    /// </remarks>
    /// <param name="id">会话标识。</param>
    public void Forget(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!sessions.TryGetValue(id, out var state)) return;
        lock (state.Gate)
        {
            state.Confirmed = null;
            if (state.Completion is null) sessions.TryRemove(new KeyValuePair<string, ResizeSessionState>(id, state));
        }
    }

    /// <summary>
    /// 丢弃 daemon 中已不存在的会话的 resize 状态，使长期运行的工作台不会为每个历史会话保留一份状态。
    /// </summary>
    /// <remarks>
    /// 仍有在途请求的会话暂不丢弃，等下次调用再处理；可以从任意线程调用。
    /// </remarks>
    /// <param name="liveSessionIds">daemon 当前仍存在的会话标识。</param>
    public void Retain(IReadOnlySet<string> liveSessionIds)
    {
        ArgumentNullException.ThrowIfNull(liveSessionIds);
        foreach (var (id, state) in sessions)
        {
            if (liveSessionIds.Contains(id)) continue;
            lock (state.Gate)
                if (state.Completion is null) sessions.TryRemove(new KeyValuePair<string, ResizeSessionState>(id, state));
        }
    }
}
