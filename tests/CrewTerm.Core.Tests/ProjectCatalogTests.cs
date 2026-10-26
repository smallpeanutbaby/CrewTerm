namespace CrewTerm.Core.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermTests", Guid.NewGuid().ToString("N"));
    public ProjectCatalogTests() => Directory.CreateDirectory(root);

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

    [Fact]
    public void MissingProjectIsRejectedWithoutChangingCatalog()
    {
        var catalog = new ProjectCatalog(Path.Combine(root, "state"));
        Assert.Throws<DirectoryNotFoundException>(() => catalog.Add(Path.Combine(root, "missing")));
        Assert.Empty(catalog.Projects);
    }

    [Fact]
    public void CorruptSettingsAreReportedAndPreserved()
    {
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var file = Path.Combine(state, "projects.json");
        File.WriteAllText(file, "broken original");
        Assert.Throws<InvalidDataException>(() => new ProjectCatalog(state));
        Assert.Equal("broken original", File.ReadAllText(file));
    }

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

    public void Dispose() => Directory.Delete(root, true);
}
