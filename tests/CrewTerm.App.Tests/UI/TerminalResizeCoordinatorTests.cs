using System.Collections.Concurrent;
using CrewTerm.App.UI.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="TerminalResizeCoordinator"/>（按会话合并终端尺寸调整请求、只发送最新尺寸的协调器）的单元测试：
/// 用可控的调整委托模拟延迟、失败和多个会话并发，验证合并、去重、重试与会话间互不阻塞。
/// </summary>
public sealed class TerminalResizeCoordinatorTests
{
    /// <summary>
    /// 验证：第一个调整（80×24）被延迟期间又请求了两个尺寸时，不会立即发送新的调整；旧调整完成后只发送最新的 120×40，跳过中间的 100×30，三个请求的任务都会完成。
    /// </summary>
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

    /// <summary>
    /// 验证：调整失败时异常传给调用方，失败的尺寸不会被记为已确认，再次请求同一尺寸会重新发送（共尝试 2 次）。
    /// </summary>
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

    /// <summary>
    /// 验证：重复请求已确认的尺寸不会再次发送，只有尺寸变化时才发送调整。
    /// </summary>
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

    /// <summary>
    /// 验证：<see cref="TerminalResizeCoordinator.Retain"/> 丢弃已不存在会话的已确认尺寸——同一标识再次出现时，
    /// 相同尺寸会重新发送；仍存在的会话保留确认，不会重复发送。
    /// </summary>
    [Fact]
    public async Task RetainForgetsSessionsThatNoLongerExist()
    {
        var calls = new ConcurrentQueue<string>();
        var coordinator = new TerminalResizeCoordinator((id, _, _) =>
        {
            calls.Enqueue(id);
            return Task.CompletedTask;
        });
        await coordinator.RequestAsync("closed", 80, 24);
        await coordinator.RequestAsync("live", 80, 24);

        coordinator.Retain(new HashSet<string>(["live"]));
        await coordinator.RequestAsync("closed", 80, 24);
        await coordinator.RequestAsync("live", 80, 24);

        Assert.Equal(["closed", "live", "closed"], calls.ToArray());
    }

    /// <summary>
    /// 验证：一个会话的调整被阻塞时，另一个会话的调整仍能立即完成并生效，而被阻塞的调整保持未完成。
    /// </summary>
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

    /// <summary>
    /// 验证：已确认 80×24 后，在 100×30 发送途中又请求回到 80×24 时，回退请求不会因与上次确认的尺寸相同而被跳过：依次发送 80×24、100×30、80×24，最终生效的是 80×24。
    /// </summary>
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

    /// <summary>
    /// 验证：已被新请求取代的旧调整失败时，不会让任何等待中的请求抛出异常，最新尺寸 120×40 照常生效。
    /// </summary>
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

    /// <summary>
    /// 验证：80×24 已确认后 100×30 发送失败（终端的实际尺寸因此未知），再请求 80×24 会重新发送，而不是因与旧的确认尺寸相同而被跳过。
    /// </summary>
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

    /// <summary>
    /// 创建一个以异步方式运行续体的 <see cref="TaskCompletionSource"/>，用作测试与调整委托之间的同步信号，避免续体在触发方线程上同步执行。
    /// </summary>
    /// <returns>新的信号源。</returns>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
