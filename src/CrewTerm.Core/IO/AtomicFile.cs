namespace CrewTerm.Core.IO;

/// <summary>
/// 以“写临时文件 → 刷盘 → 原子替换”的方式写入文件，避免中途失败留下半截内容。
/// </summary>
/// <remarks>
/// 源代码保存、检查点恢复和 JSON 配置写入都通过这里完成，保证同一套持久化语义。
/// 临时文件与目标文件位于同一目录，使 <see cref="File.Move(string, string, bool)"/>
/// 在同一卷内完成替换；失败时临时文件会被删除，目标文件保持原样。
/// 读取请使用 <see cref="ReadAllBytes"/>：它以最宽松的共享方式短暂打开文件；读取与替换恰好重叠时，替换方有限次重试，并发保存不会因此失败。
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// 替换目标文件的最大尝试次数（含第一次）。
    /// </summary>
    private const int CommitAttempts = 12;

    /// <summary>
    /// 两次替换尝试之间的基础等待时间（毫秒），按尝试次数线性增加。
    /// </summary>
    private const int CommitRetryDelayMilliseconds = 20;

    /// <summary>
    /// Win32 <c>ERROR_SHARING_VIOLATION</c>：文件正被其他进程以不兼容的共享方式打开。
    /// </summary>
    private const int SharingViolation = 32;

    /// <summary>
    /// Win32 <c>ERROR_LOCK_VIOLATION</c>：文件的某个区域被其他进程锁定。
    /// </summary>
    private const int LockViolation = 33;

    /// <summary>
    /// 原子写入字节内容。
    /// </summary>
    /// <param name="path">目标文件路径；所在目录必须已经存在。</param>
    /// <param name="content">要写入的完整内容。</param>
    /// <param name="beforeCommit">
    /// 临时文件已刷盘、替换目标之前调用的检查。抛出异常即放弃本次写入，
    /// 常用于“写入前确认磁盘文件没有被其他程序改动”的冲突检测。
    /// </param>
    /// <param name="overwrite">为 <see langword="false"/> 时目标已存在则抛出 <see cref="IOException"/>。</param>
    /// <exception cref="ArgumentException">路径没有上级目录。</exception>
    /// <exception cref="IOException">写入失败，或其他进程长时间以独占方式占用目标文件。</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content, Action? beforeCommit = null, bool overwrite = true)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The target path has no parent directory.", nameof(path));
        var temporary = Path.Combine(directory, $".crewterm-{Guid.NewGuid():N}.tmp");
        try
        {
            // WriteThrough + Flush(true) 确保替换前内容已经落到磁盘，而不仅仅是系统缓存。
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke();
            Commit(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// 以最宽松的共享方式读取文件的全部字节，尽量缩短并减少对写入方的阻碍。
    /// </summary>
    /// <remarks>
    /// 以 <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> 打开并一次性读完后立即关闭：
    /// 不阻止其他进程同时读写或删除该文件。注意 Windows 的覆盖式重命名在目标仍被打开时会被拒绝，
    /// 因此读取与另一个进程的原子替换恰好重叠时，由替换方（<see cref="WriteAllBytes"/>）在读取结束后重试完成。
    /// </remarks>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容。</returns>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 用临时文件替换目标文件；目标正被其他进程短暂占用时有限次重试。
    /// </summary>
    /// <param name="temporary">已刷盘的临时文件。</param>
    /// <param name="target">目标文件绝对路径。</param>
    /// <param name="overwrite">是否允许覆盖已存在的目标。</param>
    private static void Commit(string temporary, string target, bool overwrite)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, target, overwrite);
                return;
            }
            catch (Exception error) when (attempt < CommitAttempts && IsTransient(error))
            {
                Thread.Sleep(CommitRetryDelayMilliseconds * attempt);
            }
        }
    }

    /// <summary>
    /// 判断替换失败是否可能是暂时的（防病毒扫描、索引服务或其他读取方短暂占用目标文件）。
    /// </summary>
    /// <param name="error">替换时抛出的异常。</param>
    /// <returns>值得重试时为 <see langword="true"/>；目标已存在等确定性错误返回 <see langword="false"/>。</returns>
    private static bool IsTransient(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult & 0xFFFF) is SharingViolation or LockViolation,
        _ => false
    };
}
