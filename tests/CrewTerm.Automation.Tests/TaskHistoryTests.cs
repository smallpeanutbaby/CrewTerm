using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class TaskHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-history-" + Guid.NewGuid().ToString("N"));
    public TaskHistoryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ReopeningRestoresRealResultsAndKeepsNewestWithinLimit()
    {
        var history = new TaskHistory(root, maxEntries: 2);
        history.Add(Result("one", 1));
        history.Add(Result("two", 2));
        history.Add(Result("three", 3));
        var reopened = new TaskHistory(root, maxEntries: 2).List();
        Assert.Equal(new[] { "three", "two" }, reopened.Select(r => r.Id));
        Assert.Equal(7, reopened[0].ExitCode);
        Assert.Equal("recorded output", reopened[0].Output);
        Assert.False(reopened[0].Succeeded);
    }

    [Fact]
    public void UpdatingExistingRunDoesNotDuplicateItsId()
    {
        var first = new TaskHistory(root);
        var second = new TaskHistory(root);
        first.Add(Result("one", 1));
        second.Add(Result("two", 2));
        first.Add(Result("one", 3) with { Output = "updated" });
        var rows = second.List();
        Assert.Equal(2, rows.Count);
        Assert.Equal("updated", rows.Single(r => r.Id == "one").Output);
    }

    [Fact]
    public void CorruptHistoryIsReportedWithoutReplacingOriginalBytes()
    {
        var path = Path.Combine(root, "task-history.json");
        File.WriteAllText(path, "{broken history");
        Assert.Throws<InvalidDataException>(() => new TaskHistory(root));
        Assert.Equal("{broken history", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    public void StructurallyInvalidHistoryIsReportedWithoutOverwriting(string original)
    {
        var path = Path.Combine(root, "task-history.json");
        File.WriteAllText(path, original);
        Assert.Throws<InvalidDataException>(() => new TaskHistory(root));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidNewResultDoesNotOverwriteHistory()
    {
        var history = new TaskHistory(root);
        history.Add(Result("kept", 1));
        Assert.Throws<ArgumentException>(() => history.Add(Result("", 2)));
        Assert.Equal("kept", Assert.Single(history.List()).Id);
    }

    private TaskRunResult Result(string id, int minute)
    {
        var start = new DateTimeOffset(2026, 10, 26, 1, minute, 0, TimeSpan.Zero);
        return new(id, "test", "exit /b 7", root, start, start.AddSeconds(1), 7, "recorded output", false);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
