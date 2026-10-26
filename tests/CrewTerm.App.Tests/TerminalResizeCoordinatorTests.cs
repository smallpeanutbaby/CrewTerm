using System.Collections.Concurrent;
using CrewTerm.App.UI;

namespace CrewTerm.App.Tests;

public sealed class TerminalResizeCoordinatorTests
{
    [Fact]
    public async Task DelayedOldResizeCompletesBeforeOnlyTheLatestQueuedSizeIsApplied()
    {
        var started = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<(int Columns, int Rows)>();
        (int Columns, int Rows) applied = default;
        var coordinator = new TerminalResizeCoordinator(async (_, columns, rows) =>
        {
            calls.Enqueue((columns, rows));
            if (columns == 80) { started.SetResult(); await release.Task; }
            applied = (columns, rows);
        });

        var first = coordinator.RequestAsync("terminal", 80, 24);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var intermediate = coordinator.RequestAsync("terminal", 100, 30);
        var latest = coordinator.RequestAsync("terminal", 120, 40);
        Assert.Single(calls);
        release.SetResult();
        await Task.WhenAll(first, intermediate, latest).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(80, 24), (120, 40)], calls.ToArray());
        Assert.Equal((120, 40), applied);
    }

    [Fact]
    public async Task FailedLatestResizeDoesNotPreventRetryingTheSameSize()
    {
        var attempts = 0;
        var coordinator = new TerminalResizeCoordinator((_, _, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new IOException("connection failed"))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => coordinator.RequestAsync("terminal", 100, 30));
        await coordinator.RequestAsync("terminal", 100, 30);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RepeatedConfirmedSizeDoesNotSendAnotherResize()
    {
        var calls = new ConcurrentQueue<(int Columns, int Rows)>();
        var coordinator = new TerminalResizeCoordinator((_, columns, rows) =>
        {
            calls.Enqueue((columns, rows));
            return Task.CompletedTask;
        });

        await coordinator.RequestAsync("terminal", 80, 24);
        await coordinator.RequestAsync("terminal", 80, 24);
        await coordinator.RequestAsync("terminal", 100, 30);
        await coordinator.RequestAsync("terminal", 100, 30);

        Assert.Equal([(80, 24), (100, 30)], calls.ToArray());
    }

    [Fact]
    public async Task SlowSessionDoesNotBlockAnotherSession()
    {
        var started = Signal();
        var release = Signal();
        var applied = new ConcurrentDictionary<string, (int Columns, int Rows)>();
        var coordinator = new TerminalResizeCoordinator(async (id, columns, rows) =>
        {
            if (id == "slow") { started.SetResult(); await release.Task; }
            applied[id] = (columns, rows);
        });

        var slow = coordinator.RequestAsync("slow", 80, 24);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await coordinator.RequestAsync("fast", 120, 40).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((120, 40), applied["fast"]);
            Assert.False(slow.IsCompleted);
        }
        finally { release.TrySetResult(); await slow.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task ReturnToConfirmedSizeStillAppliesAfterDifferentInFlightSize()
    {
        var started = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<(int Columns, int Rows)>();
        (int Columns, int Rows) applied = default;
        var coordinator = new TerminalResizeCoordinator(async (_, columns, rows) =>
        {
            calls.Enqueue((columns, rows));
            if (columns == 100) { started.SetResult(); await release.Task; }
            applied = (columns, rows);
        });

        await coordinator.RequestAsync("terminal", 80, 24);
        var resized = coordinator.RequestAsync("terminal", 100, 30);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var restored = coordinator.RequestAsync("terminal", 80, 24);
        release.SetResult();
        await Task.WhenAll(resized, restored).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(80, 24), (100, 30), (80, 24)], calls.ToArray());
        Assert.Equal((80, 24), applied);
    }

    [Fact]
    public async Task FailedSupersededResizeStillAllowsNewestSizeToApply()
    {
        var started = Signal();
        var release = Signal();
        (int Columns, int Rows) applied = default;
        var coordinator = new TerminalResizeCoordinator(async (_, columns, rows) =>
        {
            if (columns == 80)
            {
                started.SetResult();
                await release.Task;
                throw new IOException("old resize failed");
            }
            applied = (columns, rows);
        });

        var first = coordinator.RequestAsync("terminal", 80, 24);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var latest = coordinator.RequestAsync("terminal", 120, 40);
        release.SetResult();
        await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal((120, 40), applied);
    }

    [Fact]
    public async Task FailedResizeMakesPreviouslyConfirmedSizeWorthSendingAgain()
    {
        var appliedCount = 0;
        var coordinator = new TerminalResizeCoordinator((_, columns, _) =>
        {
            if (columns == 100) return Task.FromException(new IOException("response lost"));
            Interlocked.Increment(ref appliedCount);
            return Task.CompletedTask;
        });

        await coordinator.RequestAsync("terminal", 80, 24);
        await Assert.ThrowsAsync<IOException>(() => coordinator.RequestAsync("terminal", 100, 30));
        await coordinator.RequestAsync("terminal", 80, 24);

        Assert.Equal(2, appliedCount);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
