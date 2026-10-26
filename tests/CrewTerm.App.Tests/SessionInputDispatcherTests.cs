using System.Collections.Concurrent;
using CrewTerm.App.UI;

namespace CrewTerm.App.Tests;

public sealed class SessionInputDispatcherTests
{
    [Fact]
    public async Task DrainWaitsForBlockedSendAndPreservesCrossSessionTextAndEnterOrder()
    {
        var entered = Signal();
        var release = Signal();
        var sent = new ConcurrentQueue<(string Session, string Text)>();
        using var dispatcher = new SessionInputDispatcher(async (id, text, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            sent.Enqueue((id, text));
        });
        Assert.True(dispatcher.TryWrite("agent-a", "echo FIRST"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatcher.TryWrite("agent-a", "\r"));
        Assert.True(dispatcher.TryWrite("agent-b", "echo SECOND"));
        Assert.True(dispatcher.TryWrite("agent-b", "\r"));

        var drain = dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5));

        Assert.False(drain.IsCompleted);
        Assert.False(dispatcher.TryWrite("agent-a", "late input"));
        release.SetResult();
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([("agent-a", "echo FIRST"), ("agent-a", "\r"), ("agent-b", "echo SECOND"), ("agent-b", "\r")], sent);
    }

    [Fact]
    public async Task DrainTimeoutCancelsTheBlockedSenderAndDoesNotSendQueuedTail()
    {
        var entered = Signal();
        var cancelled = Signal();
        var attempted = new ConcurrentQueue<string>();
        using var dispatcher = new SessionInputDispatcher(async (_, text, token) =>
        {
            attempted.Enqueue(text);
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        Assert.True(dispatcher.TryWrite("agent", "blocked text"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatcher.TryWrite("agent", "\r"));

        Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(40)));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["blocked text"], attempted);
        Assert.False(dispatcher.TryWrite("agent", "late"));
    }

    [Fact]
    public async Task SuccessfulDrainRejectsNewInputAndCanBeAwaitedAgain()
    {
        var sent = new ConcurrentQueue<string>();
        using var dispatcher = new SessionInputDispatcher((_, text, _) =>
        {
            sent.Enqueue(text);
            return Task.CompletedTask;
        });
        Assert.True(dispatcher.TryWrite("agent", "complete\r"));

        Assert.True(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));

        Assert.False(dispatcher.TryWrite("agent", "must not send"));
        Assert.True(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["complete\r"], sent);
    }

    [Fact]
    public async Task FullQueueRejectsOverflowWithoutDroppingAcceptedInputs()
    {
        var entered = Signal();
        var release = Signal();
        var sent = new ConcurrentQueue<string>();
        using var dispatcher = new SessionInputDispatcher(async (_, text, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            sent.Enqueue(text);
        });
        Assert.True(dispatcher.TryWrite("agent", "in progress"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 2048; index++) Assert.True(dispatcher.TryWrite("agent", index.ToString()));
        Assert.False(dispatcher.TryWrite("agent", "overflow"));

        release.SetResult();
        Assert.True(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2049, sent.Count);
        Assert.Equal("in progress", sent.First());
        Assert.Equal("2047", sent.Last());
        Assert.DoesNotContain("overflow", sent);
    }

    [Fact]
    public async Task SendFailureIsReportedAndLaterInputsAreStillAttemptedInOrder()
    {
        var expectedError = new IOException("send failed");
        var errors = new ConcurrentQueue<Exception>();
        var sent = new ConcurrentQueue<(string Session, string Text)>();
        using var dispatcher = new SessionInputDispatcher((id, text, _) =>
        {
            if (text == "fail") throw expectedError;
            sent.Enqueue((id, text));
            return Task.CompletedTask;
        }, errors.Enqueue);
        Assert.True(dispatcher.TryWrite("agent-a", "fail"));
        Assert.True(dispatcher.TryWrite("agent-b", "next"));
        Assert.True(dispatcher.TryWrite("agent-b", "\r"));

        Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expectedError, Assert.Single(errors));
        Assert.Equal([("agent-b", "next"), ("agent-b", "\r")], sent);
    }

    [Fact]
    public async Task ErrorObserverCannotAbandonRemainingAcceptedInputs()
    {
        var sent = new ConcurrentQueue<string>();
        using var dispatcher = new SessionInputDispatcher((_, text, _) =>
        {
            if (text == "fail") throw new IOException("send failed");
            sent.Enqueue(text);
            return Task.CompletedTask;
        }, _ => throw new InvalidOperationException("observer failed"));
        Assert.True(dispatcher.TryWrite("agent", "fail"));
        Assert.True(dispatcher.TryWrite("agent", "next\r"));

        Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(["next\r"], sent);
    }

    [Fact]
    public async Task DisposeCancelsOutstandingWorkAndRejectsInput()
    {
        var entered = Signal();
        var cancelled = Signal();
        using var dispatcher = new SessionInputDispatcher(async (_, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        Assert.True(dispatcher.TryWrite("agent", "pending"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispatcher.TryWrite("agent", "late"));
        Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TimeoutReturnsEvenIfTheSenderIgnoresCancellation()
    {
        var entered = Signal();
        var release = Signal();
        using var dispatcher = new SessionInputDispatcher(async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        Assert.True(dispatcher.TryWrite("agent", "uncooperative sender"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(await dispatcher.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(40)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(dispatcher.TryWrite("agent", "late"));
        }
        finally { release.TrySetResult(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
