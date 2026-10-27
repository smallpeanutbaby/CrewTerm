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

    /// <summary>
    /// 等待另一个进程写完一个文件后读取其全部文本：文件存在、非空，并且能以“只允许他人读取”的方式打开（写入方已经关闭文件）。
    /// </summary>
    /// <remarks>
    /// 只等文件出现就读取会与写入方竞争：在 Windows 上写入方仍打开着文件时读取会因共享冲突失败，杀毒软件扫描新文件时也会短暂占用它。
    /// 打开失败时每隔 25 毫秒重试。
    /// </remarks>
    /// <param name="path">文件路径。</param>
    /// <param name="timeout">最长等待时间；默认 15 秒。</param>
    /// <returns>文件的全部文本。</returns>
    /// <exception cref="TaskCanceledException">超时仍未读到内容。</exception>
    public static async Task<string> ReadAllTextAsync(string path, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream);
                if (stream.Length > 0) return await reader.ReadToEndAsync(deadline.Token);
            }
            catch (IOException) { }
            await Task.Delay(25, deadline.Token);
        }
    }
}
