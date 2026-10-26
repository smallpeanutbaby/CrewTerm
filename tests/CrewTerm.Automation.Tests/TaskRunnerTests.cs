using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class TaskRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-task-" + Guid.NewGuid().ToString("N"));

    public TaskRunnerTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task CapturesRealStandardOutputErrorAndNonzeroExitCode()
    {
        var streamed = new List<string>();
        var command = OperatingSystem.IsWindows() ? "echo hello & echo failure 1>&2 & exit /b 7" : "echo hello; echo failure >&2; exit 7";
        var result = await new TaskRunner().RunAsync(new("check", command, root), streamed.Add);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("hello", result.Output);
        Assert.Contains("failure", result.Output);
        Assert.Contains("hello", string.Concat(streamed));
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.True(result.FinishedAt >= result.StartedAt);
    }

    [Fact]
    public async Task UsesWorkingDirectoryIncludingSpaces()
    {
        var working = Directory.CreateDirectory(Path.Combine(root, "a directory")).FullName;
        var result = await new TaskRunner().RunAsync(new("cwd", "echo actual > output.txt", working));
        Assert.True(result.Succeeded);
        Assert.Contains("actual", File.ReadAllText(Path.Combine(working, "output.txt")));
        Assert.False(File.Exists(Path.Combine(root, "output.txt")));
    }

    [Fact]
    public async Task CancellationTerminatesRunningCommandAndReturnsRecordedCancellation()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = OperatingSystem.IsWindows() ? "echo started & ping -n 60 127.0.0.1 >nul" : "echo started; sleep 60";
        var run = new TaskRunner().RunAsync(new("wait", command, root), text =>
        {
            if (text.Contains("started")) started.TrySetResult();
        }, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SingleSlotDoesNotOverlapCommands()
    {
        var runner = new TaskRunner(maxConcurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = OperatingSystem.IsWindows() ? "echo first & ping -n 3 127.0.0.1 >nul" : "echo first; sleep 2";
        var first = runner.RunAsync(new("first", command, root), text =>
        {
            if (text.Contains("first")) started.TrySetResult();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = runner.RunAsync(new("second", "echo second", root));
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(results[0].FinishedAt <= results[1].StartedAt);
        Assert.All(results, r => Assert.True(r.Succeeded));
    }

    [Fact]
    public async Task BoundedOutputKeepsRecentTailAndMarksTruncation()
    {
        var command = OperatingSystem.IsWindows() ? "for /L %i in (1,1,200) do @echo line-%i" : "seq 1 200";
        var result = await new TaskRunner(maxOutputCharacters: 100).RunAsync(new("verbose", command, root));
        Assert.True(result.Succeeded);
        Assert.True(result.OutputTruncated);
        Assert.True(result.Output.Length <= 100);
        Assert.Contains("200", result.Output);
    }

    [Fact]
    public async Task CancelledQueuedCommandDoesNotExecute()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await new TaskRunner().RunAsync(new("never", "echo bad > marker", root), cancellationToken: cancelled.Token);
        Assert.True(result.Cancelled);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(Path.Combine(root, "marker")));
    }

    [Fact]
    public async Task MissingWorkingDirectoryIsRejectedBeforeLaunch()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new TaskRunner().RunAsync(new("bad", "echo bad", Path.Combine(root, "missing"))));
    }

    [Fact]
    public async Task EmptyCommandIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskRunner().RunAsync(new("bad", " ", root)));
    }

    [Fact]
    public async Task QuotedExecutableAndArgumentWithSpacesRunUnchanged()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateDirectory(Path.Combine(root, "commands with spaces")).FullName;
        var script = Path.Combine(directory, "show argument.cmd");
        File.WriteAllText(script, "@echo off\r\necho [%~1]\r\n", System.Text.Encoding.ASCII);
        var result = await new TaskRunner().RunAsync(new("quoted", $"\"{script}\" \"a b\"", root));
        Assert.Contains("[a b]", result.Output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ChineseShellOutputRemainsReadable()
    {
        var result = await new TaskRunner().RunAsync(new("unicode", "echo 你好终端", root));
        Assert.True(result.Succeeded);
        Assert.Contains("你好终端", result.Output);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 0)]
    public void InvalidLimitsAreRejected(int concurrency, int output)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TaskRunner(concurrency, output));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
