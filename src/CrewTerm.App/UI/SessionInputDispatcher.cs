using System.Threading.Channels;

namespace CrewTerm.App.UI;

/// <summary>Preserves input order and drains accepted input before a workspace detaches.</summary>
public sealed class SessionInputDispatcher : IDisposable
{
    private readonly Func<string, string, CancellationToken, Task> send;
    private readonly Action<Exception>? onError;
    private readonly Channel<(string SessionId, string Text)> queue = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly Task<bool> worker;
    private int disposed;

    public SessionInputDispatcher(Func<string, string, CancellationToken, Task> send, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        this.send = send;
        this.onError = onError;
        token = cancellation.Token;
        worker = Task.Run(ProcessAsync);
    }

    public bool TryWrite(string sessionId, string text) => queue.Writer.TryWrite((sessionId, text));

    /// <summary>
    /// Stops accepting input and waits for every accepted item. Returns false on send failure,
    /// cancellation, or timeout. Only a timeout cancels an otherwise active sender.
    /// </summary>
    public async Task<bool> CompleteAndDrainAsync(TimeSpan timeout)
    {
        queue.Writer.TryComplete();
        try { return await worker.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { CancelWorker(); return false; }
    }

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

    private void ReportError(Exception error)
    {
        try { onError?.Invoke(error); }
        catch (Exception observerError) when (observerError is not OutOfMemoryException)
        {
            // A failed error observer must not abandon the rest of the accepted input.
        }
    }

    private void CancelWorker()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException error) { ReportError(error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        CancelWorker();
        // Keep the token source usable until even a slow sender has finished its cleanup.
        _ = worker.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
