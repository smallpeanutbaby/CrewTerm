using System.Collections.Concurrent;

namespace CrewTerm.App.UI;

/// <summary>Serializes each terminal's resize requests while retaining only its newest pending size.</summary>
public sealed class TerminalResizeCoordinator(Func<string, int, int, Task> resize)
{
    private readonly ConcurrentDictionary<string, SessionState> sessions = new(StringComparer.Ordinal);

    /// <summary>Completes when the current batch reaches its newest requested size, or that resize fails.</summary>
    public Task RequestAsync(string id, int columns, int rows)
    {
        var state = sessions.GetOrAdd(id, _ => new());
        TaskCompletionSource completion;
        lock (state.Gate)
        {
            var requested = new Size(columns, rows);
            state.Latest = requested;
            if (state.Completion is not null) return state.Completion.Task;
            if (state.Confirmed == requested) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Completion = completion;
        }
        _ = ApplyLatestAsync(id, state, completion);
        return completion.Task;
    }

    private async Task ApplyLatestAsync(string id, SessionState state, TaskCompletionSource completion)
    {
        while (true)
        {
            Size requested;
            lock (state.Gate) requested = state.Latest;

            Exception? failure = null;
            try { await resize(id, requested.Columns, requested.Rows).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }

            lock (state.Gate)
            {
                // A lost response leaves the remote size unknown, even if an older size was confirmed.
                state.Confirmed = failure is null ? requested : null;
                if (state.Latest != requested) continue;
                state.Completion = null;
                if (failure is null) completion.SetResult();
                else completion.SetException(failure);
                return;
            }
        }
    }

    private readonly record struct Size(int Columns, int Rows);

    private sealed class SessionState
    {
        public object Gate { get; } = new();
        public Size Latest { get; set; }
        public Size? Confirmed { get; set; }
        public TaskCompletionSource? Completion { get; set; }
    }
}
