namespace CrewTerm.Testing.Fixtures;

/// <summary>
/// 轮询等待真实进程、伪终端或文件系统达到预期状态。
/// </summary>
public static class Poll
{
    /// <summary>
    /// 默认的最长等待时间；按多个测试程序集并行、CPU 被占满的情况放宽，条件成立时立即返回。
    /// </summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 每隔 25 毫秒检查一次条件，直到条件成立。
    /// </summary>
    /// <param name="condition">要等待成立的条件。</param>
    /// <param name="timeout">最长等待时间；默认 15 秒。</param>
    /// <returns>条件成立时完成的任务。</returns>
    /// <exception cref="TaskCanceledException">超时仍未成立。</exception>
    public static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? DefaultTimeout);
        while (!condition()) await Task.Delay(25, deadline.Token);
    }
}
