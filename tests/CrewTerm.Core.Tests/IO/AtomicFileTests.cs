using CrewTerm.Core.IO;

namespace CrewTerm.Core.Tests.IO;

/// <summary>
/// 覆盖 <see cref="AtomicFile"/> 的共享读取与 <see cref="FileSystemPath.IsWithin"/> 的路径包含判断（真实临时目录）。
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    /// <summary>
    /// 本测试实例独占的临时目录。
    /// </summary>
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CrewTermAtomicFile", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// 验证：另一个读取方短暂打开目标文件（约 150 毫秒）期间开始的原子替换不会失败——替换方在读取方关闭后重试成功，
    /// 之后读取得到新内容。
    /// </summary>
    /// <returns>表示测试过程的任务。</returns>
    [Fact]
    public async Task AtomicReplaceRetriesWhileAnotherReaderBrieflyHoldsTheFile()
    {
        var path = Path.Combine(root, "state.json");
        AtomicFile.WriteAllBytes(path, "old"u8);
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await reader.DisposeAsync();
        });
        AtomicFile.WriteAllBytes(path, "new"u8);
        await release;
        Assert.Equal("new"u8.ToArray(), AtomicFile.ReadAllBytes(path));
    }

    /// <summary>
    /// 验证：路径包含判断接受根目录本身与其子路径（忽略末尾分隔符与大小写），拒绝同名前缀的兄弟目录和经 <c>..</c> 越出根目录的路径。
    /// </summary>
    [Fact]
    public void IsWithinRejectsSiblingPrefixesAndParentEscapes()
    {
        var app = Path.Combine(root, "app");
        Assert.True(FileSystemPath.IsWithin(app, app + Path.DirectorySeparatorChar));
        Assert.True(FileSystemPath.IsWithin(app, Path.Combine(app, "src", "a.cs")));
        Assert.False(FileSystemPath.IsWithin(app, Path.Combine(root, "app-data", "a.cs")));
        Assert.False(FileSystemPath.IsWithin(app, Path.Combine(app, "..", "outside.txt")));
        if (OperatingSystem.IsWindows()) Assert.True(FileSystemPath.IsWithin(app.ToUpperInvariant(), Path.Combine(app, "x")));
    }

    /// <summary>
    /// 删除临时目录。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
