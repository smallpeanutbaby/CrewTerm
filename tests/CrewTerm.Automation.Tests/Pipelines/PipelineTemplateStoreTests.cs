using CrewTerm.Automation.Pipelines;

namespace CrewTerm.Automation.Tests.Pipelines;

/// <summary>
/// 覆盖 <see cref="PipelineTemplateStore"/> 的流水线模板保存、按标识更新与删除、非法模板拒绝和损坏文件保护，
/// 以及保存的模板经 <see cref="PipelineRunner"/> 在指定项目中真实运行。
/// 每个测试在系统临时目录下独立的子目录中读写真实的 <c>pipeline-templates.json</c>，测试结束后整体删除。
/// </summary>
public sealed class PipelineTemplateStoreTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录，既用作模板存储的状态目录，也用作运行模板的项目目录。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-template-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public PipelineTemplateStoreTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：保存含两个步骤的模板后重新加载，展开到所选项目目录再运行，两步按 <c>prepare</c>、<c>test</c> 的顺序成功执行，
    /// 且第一步在项目目录中写出了 <c>marker.txt</c>。
    /// </summary>
    [Fact]
    public async Task SavedTemplateRunsItsStepsInSelectedProject()
    {
        var store = new PipelineTemplateStore(root);
        store.Upsert(new("check", "Project checks", [new("prepare", "echo one > marker.txt"), new("test", "echo two")]));
        var loaded = Assert.Single(new PipelineTemplateStore(root).List());
        var commands = loaded.ForProject(root);
        var result = await new PipelineRunner().RunAsync(commands);
        Assert.True(result.Succeeded);
        Assert.Contains("one", File.ReadAllText(Path.Combine(root, "marker.txt")));
        Assert.Equal(new[] { "prepare", "test" }, result.Steps.Select(s => s.Name));
    }

    /// <summary>
    /// 验证：以相同标识再次保存会覆盖原模板（只剩名称为 <c>After</c> 的一条）；按该标识删除返回 <see langword="true"/>，
    /// 重新加载后列表为空。
    /// </summary>
    [Fact]
    public void UpdateAndRemoveUseStableTemplateId()
    {
        var store = new PipelineTemplateStore(root);
        store.Upsert(new("check", "Before", [new("test", "echo before")]));
        store.Upsert(new("check", "After", [new("test", "echo after")]));
        Assert.Equal("After", Assert.Single(store.List()).Name);
        Assert.True(store.Remove("check"));
        Assert.Empty(new PipelineTemplateStore(root).List());
    }

    /// <summary>
    /// 验证：用含空命令步骤的非法模板覆盖同一标识时抛出 <see cref="ArgumentException"/>，原有的有效模板保持不变。
    /// </summary>
    [Fact]
    public void InvalidTemplateNeverReplacesValidTemplate()
    {
        var store = new PipelineTemplateStore(root);
        store.Upsert(new("check", "Valid", [new("test", "echo valid")]));
        Assert.Throws<ArgumentException>(() => store.Upsert(new("check", "Invalid", [new("test", "")])));
        Assert.Equal("Valid", Assert.Single(store.List()).Name);
    }

    /// <summary>
    /// 验证：模板文件不是有效 JSON、含缺少字段的空对象或 <c>null</c> 元素时，构造存储抛出 <see cref="InvalidDataException"/>，
    /// 文件原始内容保持不变。
    /// </summary>
    /// <param name="original">写入模板文件的损坏内容。</param>
    [Theory]
    [InlineData("not json")]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    public void CorruptionIsReportedAndOriginalFileRemains(string original)
    {
        var path = Path.Combine(root, "pipeline-templates.json");
        File.WriteAllText(path, original);
        Assert.Throws<InvalidDataException>(() => new PipelineTemplateStore(root));
        Assert.Equal(original, File.ReadAllText(path));
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
