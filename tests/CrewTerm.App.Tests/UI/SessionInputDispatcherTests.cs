using System.Collections.Concurrent;
using CrewTerm.App.UI.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="SessionInputDispatcher"/>（把界面键入按顺序异步发送给 daemon 的输入队列）的单元测试：
/// 用可控的发送委托模拟阻塞、取消、失败和不配合取消的发送方，验证发送顺序、排空、超时、容量上限、错误报告与释放行为。
/// </summary>
public sealed class SessionInputDispatcherTests
{
    /// <summary>
    /// 验证：首个发送被阻塞时，跨会话写入的文本与回车仍按写入顺序排队；<c>CompleteAndDrainAsync</c> 会等待阻塞的发送（期间尚未完成且拒绝新的输入），
    /// 放行后返回 true，并按原顺序发送全部四条输入。
    /// </summary>
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

    /// <summary>
    /// 验证：排空在 40 毫秒内未完成时返回 false 并取消正在阻塞的发送，队列中剩余的回车不再发送；再次排空仍返回 false，之后的写入被拒绝。
    /// </summary>
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

    /// <summary>
    /// 验证：排空成功时返回 true，此后的写入被拒绝；再次调用排空同样返回 true，且只发送过排空前接受的那条输入。
    /// </summary>
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

    /// <summary>
    /// 验证：一条输入正在发送时，队列还能再接受 2048 条，超出的那条被拒绝；放行后已接受的 2049 条全部按顺序发送，被拒绝的输入不会出现。
    /// </summary>
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

    /// <summary>
    /// 验证：某条输入发送失败时，原异常交给错误观察者，后续输入（包括其他会话的）仍按顺序发送；排空返回 false 以反映曾有发送失败。
    /// </summary>
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

    /// <summary>
    /// 验证：错误观察者自身抛出异常时，分发器也不会放弃其余已接受的输入：后续输入照常发送，排空返回 false。
    /// </summary>
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

    /// <summary>
    /// 验证：<c>Dispose</c> 会取消正在进行的发送，此后写入被拒绝，排空返回 false。
    /// </summary>
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

    /// <summary>
    /// 验证：发送委托完全忽略取消令牌时，排空仍会在 40 毫秒超时后按时返回 false，并拒绝之后的写入。
    /// </summary>
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

    /// <summary>
    /// 创建一个以异步方式运行续体的 <see cref="TaskCompletionSource"/>，用作测试与发送委托之间的同步信号，避免续体在触发方线程上同步执行。
    /// </summary>
    /// <returns>新的信号源。</returns>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
