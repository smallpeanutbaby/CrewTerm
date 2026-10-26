using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class PipelineTemplateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-template-" + Guid.NewGuid().ToString("N"));
    public PipelineTemplateStoreTests() => Directory.CreateDirectory(root);

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

    [Fact]
    public void InvalidTemplateNeverReplacesValidTemplate()
    {
        var store = new PipelineTemplateStore(root);
        store.Upsert(new("check", "Valid", [new("test", "echo valid")]));
        Assert.Throws<ArgumentException>(() => store.Upsert(new("check", "Invalid", [new("test", "")])));
        Assert.Equal("Valid", Assert.Single(store.List()).Name);
    }

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

    public void Dispose() => Directory.Delete(root, recursive: true);
}
