namespace CrewTerm.Testing.Fixtures;

/// <summary>
/// 测试结束时删除测试自己创建的临时目录。
/// </summary>
public static class TemporaryDirectory
{
    /// <summary>
    /// 尽力递归删除目录：测试启动的进程刚被结束时（Windows 上结束进程树是异步的）或杀毒软件正在扫描时，其中的文件可能仍被短暂占用；
    /// 删除失败时最多重试 10 秒，期限内仍失败则放弃，不让清理失败掩盖测试结果。
    /// </summary>
    /// <param name="path">要删除的目录；不存在时直接返回。</param>
    public static void Delete(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return;
                Thread.Sleep(100);
            }
        }
    }
}
