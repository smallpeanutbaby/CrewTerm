using CrewTerm.Core.Projects;

namespace CrewTerm.Core.Tests.Projects;

/// <summary>
/// 覆盖 <see cref="ProjectCatalog"/> 的项目增删、持久化、损坏文件保护与多客户端乐观并发行为。
/// 每个测试在系统临时目录下独立的子目录中读写真实的 <c>projects.json</c> 与项目文件夹，测试结束后整体删除。
/// </summary>
public sealed class ProjectCatalogTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录，既存放状态目录，也存放用作项目的文件夹。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermTests", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public ProjectCatalogTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：同一个（路径含空格的）项目以带与不带末尾分隔符两种形式添加只会登记一次；保存后重新加载目录，
    /// 恢复出的唯一项目保留原有标识、中文名称“我的项目”和规范化路径。
    /// </summary>
    [Fact]
    public void ProjectsSurviveRestartWithoutDuplicates()
    {
        var state = Path.Combine(root, "state");
        var project = Directory.CreateDirectory(Path.Combine(root, "project space")).FullName;
        var catalog = new ProjectCatalog(state);
        var first = catalog.Add(project, "我的项目");
        catalog.Add(project + Path.DirectorySeparatorChar);
        catalog.Save();
        var restored = Assert.Single(new ProjectCatalog(state).Projects);
        Assert.Equal(first.Id, restored.Id);
        Assert.Equal("我的项目", restored.Name);
        Assert.Equal(project, restored.Path);
    }

    /// <summary>
    /// 验证：添加不存在的目录时抛出 <see cref="DirectoryNotFoundException"/>，项目列表保持为空。
    /// </summary>
    [Fact]
    public void MissingProjectIsRejectedWithoutChangingCatalog()
    {
        var catalog = new ProjectCatalog(Path.Combine(root, "state"));
        Assert.Throws<DirectoryNotFoundException>(() => catalog.Add(Path.Combine(root, "missing")));
        Assert.Empty(catalog.Projects);
    }

    /// <summary>
    /// 验证：<c>projects.json</c> 内容不是有效 JSON 时，构造 <see cref="ProjectCatalog"/> 抛出 <see cref="InvalidDataException"/>，
    /// 且文件的原始内容保持不变。
    /// </summary>
    [Fact]
    public void CorruptSettingsAreReportedAndPreserved()
    {
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var file = Path.Combine(state, "projects.json");
        File.WriteAllText(file, "broken original");
        Assert.Throws<InvalidDataException>(() => new ProjectCatalog(state));
        Assert.Equal("broken original", File.ReadAllText(file));
    }

    /// <summary>
    /// 验证：从目录中移除项目并保存后，项目列表为空，但磁盘上的项目文件夹仍然存在（只移除登记，不删除文件）。
    /// </summary>
    [Fact]
    public void RemovingProjectDoesNotRemoveItsFiles()
    {
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var catalog = new ProjectCatalog(Path.Combine(root, "state"));
        var entry = catalog.Add(project);
        catalog.Remove(entry.Id);
        catalog.Save();
        Assert.Empty(catalog.Projects);
        Assert.True(Directory.Exists(project));
    }

    /// <summary>
    /// 验证：两个客户端基于同一份状态加载目录，第一个先保存后，第二个基于过期内容保存时抛出 <see cref="IOException"/>，
    /// 磁盘上只保留第一个客户端添加的项目，不会被静默覆盖。
    /// </summary>
    [Fact]
    public void ConcurrentClientsCannotSilentlyOverwriteProjects()
    {
        var state = Path.Combine(root, "state");
        var first = new ProjectCatalog(state);
        var second = new ProjectCatalog(state);
        var a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        first.Add(a);
        first.Save();
        second.Add(b);
        Assert.Throws<IOException>(() => second.Save());
        Assert.Equal(a, Assert.Single(new ProjectCatalog(state).Projects).Path);
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, true);
}
