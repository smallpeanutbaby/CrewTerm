namespace CrewTerm.App.Tests.Fixtures;

/// <summary>
/// 测试结束时清理临时状态目录，避免系统临时目录中不断堆积 daemon 状态文件。
/// </summary>
internal static class TestStateDirectory
{
    /// <summary>
    /// 尽力删除状态目录：daemon 停止后可能仍短暂占用文件，最多重试 10 秒，期限内仍失败则放弃，不让清理失败掩盖测试结果。
    /// </summary>
    /// <param name="path">要删除的状态目录；不存在时直接返回。</param>
    /// <returns>删除完成或放弃时完成的任务。</returns>
    public static async Task DeleteAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return;
                await Task.Delay(100);
            }
        }
    }
}
