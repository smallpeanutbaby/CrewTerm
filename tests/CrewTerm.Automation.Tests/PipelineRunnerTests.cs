using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class PipelineRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-pipeline-" + Guid.NewGuid().ToString("N"));
    public PipelineRunnerTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task FailurePreventsLaterStepFromChangingFiles()
    {
        var result = await new PipelineRunner().RunAsync([
            new("fail", OperatingSystem.IsWindows() ? "exit /b 9" : "exit 9", root),
            new("never", "echo bad > should-not-exist", root)
        ]);
        Assert.Single(result.Steps);
        Assert.Equal(9, result.Steps[0].ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(root, "should-not-exist")));
    }

    [Fact]
    public async Task SuccessfulStepsRunInDependencyOrder()
    {
        var read = OperatingSystem.IsWindows() ? "type previous.txt" : "cat previous.txt";
        var result = await new PipelineRunner().RunAsync([
            new("produce", "echo handoff > previous.txt", root),
            new("consume", read, root)
        ]);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("handoff", result.Steps[1].Output);
        Assert.True(result.Steps[0].FinishedAt <= result.Steps[1].StartedAt);
    }

    [Fact]
    public async Task PreCancelledPipelineExecutesNothing()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        var result = await new PipelineRunner().RunAsync([new("never", "echo bad > marker", root)], cancellationToken: token.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(root, "marker")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
