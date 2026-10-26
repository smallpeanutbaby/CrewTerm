namespace CrewTerm.Core.Tests;

public sealed class WorkspaceSettingsTests
{
    [Fact]
    public void TemplatesAndLayoutSurviveRestart()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceSettingsStore(state);
            var settings = store.Load();
            settings.SplitTerminals = true;
            settings.MaximumConcurrentTasks = 2;
            settings.Templates.Add(new("verify", "dotnet test", "D:\\代码\\project"));
            store.Save(settings);
            var restored = store.Load();
            Assert.True(restored.SplitTerminals);
            Assert.Equal(2, restored.MaximumConcurrentTasks);
            Assert.Equal("dotnet test", Assert.Single(restored.Templates).Command);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void InvalidConcurrencyCannotBeSaved(int count)
    {
        var store = new WorkspaceSettingsStore(Path.GetTempPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new() { MaximumConcurrentTasks = count }));
    }

    [Fact]
    public void ContextIncludesOnlyRequestedLinesAndCorrectRelativeLocation()
    {
        var root = Path.GetFullPath(".");
        var result = CodeContext.Build(root, Path.Combine(root, "file.cs"), "first\r\nsecond\r\nthird", 2, 2, "Review this");
        Assert.Contains("file.cs:2-2", result);
        Assert.Contains("second", result);
        Assert.DoesNotContain("first", result);
        Assert.DoesNotContain("third", result);
    }

    [Fact]
    public void ContextRejectsInvalidLineRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeContext.Build(Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "file.cs"), "one", 2, 4, "review"));
    }

    [Fact]
    public void ConcurrentTemplateEditsAreReportedInsteadOfOverwritten()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new WorkspaceSettingsStore(state);
            var second = new WorkspaceSettingsStore(state);
            var a = first.Load();
            var b = second.Load();
            a.Templates.Add(new("A", "echo A", state));
            first.Save(a);
            b.Templates.Add(new("B", "echo B", state));
            Assert.Throws<IOException>(() => second.Save(b));
            Assert.Equal("A", Assert.Single(first.Load().Templates).Name);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }
}
