using System.Runtime.Versioning;
using CrewTerm.Core.IO;

namespace CrewTerm.Core.Tests.IO;

/// <summary>
/// 覆盖 <see cref="AtomicJsonStore"/>、<see cref="AtomicFile"/> 与 <see cref="FileSystemPath"/> 的文件读写与路径规范化行为。
/// 每个测试在系统临时目录下独立的子目录中操作真实文件，测试结束后整体删除。
/// </summary>
public sealed class AtomicJsonStoreTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录（名称带随机 GUID，避免并行测试互相干扰）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-json-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public AtomicJsonStoreTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：40 个并行写入者各自通过 <see cref="AtomicJsonStore.Update{T}"/> 向同一 JSON 列表追加一个数字后，
    /// 文件中完整保留 0 到 39 全部数字，且目录中没有残留的 <c>*.tmp</c> 临时文件。
    /// </summary>
    [Fact]
    public void ConcurrentUpdatesFromIndependentWritersAreAllRetained()
    {
        var path = Path.Combine(root, "rows.json");
        Parallel.For(0, 40, value => AtomicJsonStore.Update(path, () => new List<int>(), rows => { rows.Add(value); return true; }));
        Assert.Equal(Enumerable.Range(0, 40), AtomicJsonStore.Read(path, () => new List<int>()).Order());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>
    /// 验证：修改回调返回 <see langword="false"/>（放弃写回）时，<see cref="AtomicJsonStore.Update{T}"/> 返回
    /// <see langword="false"/>，且不会创建原本不存在的文件。
    /// </summary>
    [Fact]
    public void DeclinedUpdateDoesNotCreateOrRewriteTheFile()
    {
        var path = Path.Combine(root, "untouched.json");
        Assert.False(AtomicJsonStore.Update(path, () => new List<int>(), _ => false));
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// 验证：目标文件内容是损坏的 JSON（<c>{broken</c>）时，<see cref="AtomicJsonStore.Update{T}"/> 抛出
    /// <see cref="InvalidDataException"/>，并保留文件原有字节不被覆盖。
    /// </summary>
    [Fact]
    public void UpdateRefusesCorruptFileAndPreservesItsBytes()
    {
        var path = Path.Combine(root, "corrupt.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<InvalidDataException>(() => AtomicJsonStore.Update(path, () => new List<int>(), rows => { rows.Add(1); return true; }));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    /// <summary>
    /// 验证：<see cref="AtomicFile.WriteAllBytes"/> 的提交前检查回调抛出 <see cref="IOException"/> 时，
    /// 异常向外传播，原文件内容保持为 <c>original</c>，目录中只剩原文件而没有遗留临时文件。
    /// </summary>
    [Fact]
    public void FailedPreCommitCheckLeavesOriginalFileAndNoTemporaryFile()
    {
        var path = Path.Combine(root, "source.txt");
        File.WriteAllText(path, "original");
        Assert.Throws<IOException>(() => AtomicFile.WriteAllBytes(path, "replacement"u8, () => throw new IOException("changed")));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    /// <summary>
    /// 验证：<see cref="FileSystemPath.Normalize"/> 会去掉目录路径末尾的分隔符，但保留盘符根目录（如 <c>C:\</c>）的原样；
    /// 在 Windows 上 <see cref="FileSystemPath.AreSame"/> 比较路径时不区分大小写。
    /// </summary>
    [Fact]
    public void PathNormalizationRemovesTrailingSeparatorButKeepsDriveRoot()
    {
        var directory = Path.Combine(root, "project");
        Assert.Equal(directory, FileSystemPath.Normalize(directory + Path.DirectorySeparatorChar));
        var driveRoot = Path.GetPathRoot(root)!;
        Assert.Equal(driveRoot, FileSystemPath.Normalize(driveRoot));
        if (OperatingSystem.IsWindows()) Assert.True(FileSystemPath.AreSame(directory.ToUpperInvariant(), directory));
    }

    /// <summary>
    /// 验证（Linux / macOS）：保存状态文件时如果状态目录还不存在，新建的目录只允许当前用户访问（<c>0700</c>）——
    /// 工作台第一次写入 <c>projects.json</c> 等文件时可能比其他入口更早创建状态目录。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void SaveCreatesPrivateStateDirectoryOnUnix()
    {
        var state = Path.Combine(root, "new state");
        AtomicJsonStore.Save(StateFiles.PathOf(state, StateFiles.Projects), new List<int> { 1 });
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(state));
    }

    /// <summary>
    /// 验证（Linux / macOS）：<see cref="FileSystemPath.ResolveLinks(string)"/> 把经过目录符号链接的路径解析到真实位置，尚不存在的末尾部分原样保留，
    /// 因此 <see cref="FileSystemPath.AreSameLocation"/> 认为链接路径与真实路径是同一位置，而纯文本比较 <see cref="FileSystemPath.AreSame"/> 不认为。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void ResolveLinksFollowsDirectorySymlinks()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, real);
        var expected = Path.Combine(FileSystemPath.ResolveLinks(real), "child", "missing");
        Assert.Equal(expected, FileSystemPath.ResolveLinks(Path.Combine(link, "child", "missing") + Path.DirectorySeparatorChar));
        Assert.True(FileSystemPath.AreSameLocation(link, real));
        Assert.False(FileSystemPath.AreSame(FileSystemPath.Normalize(link), FileSystemPath.Normalize(real)));
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
