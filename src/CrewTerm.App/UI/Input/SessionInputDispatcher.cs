using System.Threading.Channels;

namespace CrewTerm.App.UI.Input;

/// <summary>
/// 终端输入的有序发送队列：保证按键顺序，并在工作台分离（detach）前把已接受的输入全部送达。
/// </summary>
/// <remarks>
/// UI 线程只负责入队（<see cref="TryWrite"/> 永不阻塞）；单个后台读取者按顺序逐条调用发送委托。
/// 某条发送失败会通过 <c>onError</c> 报告，但不会丢弃其后的输入。
/// </remarks>
public sealed class SessionInputDispatcher : IDisposable
{
    /// <summary>
    /// 实际把一条输入送到 daemon 的委托，参数依次为会话标识、文本和取消令牌；只由后台发送者调用。
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task> send;

    /// <summary>
    /// 单条发送失败时的回调；为 <see langword="null"/> 时失败只体现在 <see cref="CompleteAndDrainAsync"/> 的返回值中。
    /// </summary>
    /// <remarks>
    /// 在后台线程调用，回调需要自行切回 UI 线程；回调抛出的异常会被吞掉。
    /// </remarks>
    private readonly Action<Exception>? onError;

    /// <summary>
    /// 待发送输入的有界队列（容量 2048 条，单读取者）。
    /// </summary>
    /// <remarks>
    /// <see cref="BoundedChannelFullMode.Wait"/> 只影响异步写入；这里只用 <see cref="ChannelWriter{T}.TryWrite"/>，
    /// 队列已满时直接返回 <see langword="false"/>，UI 线程绝不会被阻塞。
    /// </remarks>
    private readonly Channel<(string SessionId, string Text)> queue = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>
    /// 用于取消后台发送者的取消源；等后台任务结束后才释放（见 <see cref="Dispose"/>）。
    /// </summary>
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>
    /// 构造时从 <see cref="cancellation"/> 预先取出的取消令牌。
    /// </summary>
    /// <remarks>
    /// 预先取出令牌：<see cref="CancellationTokenSource"/> 被释放之后不能再访问其 <see cref="CancellationTokenSource.Token"/> 属性，
    /// 而后台发送者可能在释放之后仍在检查令牌。
    /// </remarks>
    private readonly CancellationToken token;

    /// <summary>
    /// 后台发送循环任务；结果为 <see langword="true"/> 表示所有已接受的输入都发送成功且未被取消。
    /// </summary>
    private readonly Task<bool> worker;

    /// <summary>
    /// 是否已调用 <see cref="Dispose"/>：0 表示未释放，1 表示已释放；用 <see cref="Interlocked.Exchange(ref int, int)"/> 保证只释放一次。
    /// </summary>
    private int disposed;

    /// <summary>
    /// 创建队列并启动后台发送者。
    /// </summary>
    /// <param name="send">发送委托：会话标识、文本、取消令牌。</param>
    /// <param name="onError">发送失败时的回调（在后台线程调用）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="send"/> 为 <see langword="null"/>。</exception>
    public SessionInputDispatcher(Func<string, string, CancellationToken, Task> send, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        this.send = send;
        this.onError = onError;
        token = cancellation.Token;
        worker = Task.Run(ProcessAsync);
    }

    /// <summary>
    /// 把输入加入队列。
    /// </summary>
    /// <param name="sessionId">目标会话。</param>
    /// <param name="text">要发送的原始文本（按键编码、粘贴内容等）。</param>
    /// <returns>队列已满或已停止接受输入时为 <see langword="false"/>，输入不会被发送。</returns>
    public bool TryWrite(string sessionId, string text) => queue.Writer.TryWrite((sessionId, text));

    /// <summary>
    /// 停止接受新输入，并等待已接受的输入全部发送完毕。
    /// 发送失败、被取消或超时都返回 <see langword="false"/>；只有超时才会主动取消仍在进行的发送。
    /// </summary>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>全部输入都已成功发送时为 <see langword="true"/>。</returns>
    public async Task<bool> CompleteAndDrainAsync(TimeSpan timeout)
    {
        queue.Writer.TryComplete();
        try { return await worker.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { CancelWorker(); return false; }
    }

    /// <summary>
    /// 后台发送循环。
    /// </summary>
    /// <returns>所有输入都发送成功且未被取消时为 <see langword="true"/>。</returns>
    private async Task<bool> ProcessAsync()
    {
        var succeeded = true;
        try
        {
            await foreach (var input in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                try { await send(input.SessionId, input.Text, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    succeeded = false;
                    ReportError(error);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        return succeeded && !token.IsCancellationRequested;
    }

    /// <summary>
    /// 调用错误回调，并吞掉回调自身抛出的异常。
    /// </summary>
    /// <param name="error">发送失败的原因。</param>
    private void ReportError(Exception error)
    {
        try { onError?.Invoke(error); }
        catch (Exception observerError) when (observerError is not OutOfMemoryException)
        {
            // 错误回调本身失败也不能中断后续已接受输入的发送。
        }
    }

    /// <summary>
    /// 取消后台发送者（正在进行的发送会收到取消信号）。
    /// </summary>
    private void CancelWorker()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException error) { ReportError(error); }
    }

    /// <summary>
    /// 停止接受输入并取消发送者；未发送的输入被丢弃。需要保证送达时先调用 <see cref="CompleteAndDrainAsync"/>。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        CancelWorker();
        // 等发送者（即使很慢）完成清理后再释放取消源，避免它访问已释放的对象。
        _ = worker.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
