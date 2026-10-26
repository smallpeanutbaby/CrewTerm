using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Tests.Tasks;

/// <summary>
/// 覆盖 <see cref="TaskRunner"/> 通过系统 Shell 运行用户命令的行为：输出捕获与实时转发、工作目录、取消、并发槽位、
/// 输出上限、启动前校验、带空格的引号参数、中文输出、Shell 元字符的字面传递以及构造参数校验。
/// 所有命令都以真实进程运行，在系统临时目录下独立的工作目录中读写文件，测试结束后整体删除。
/// </summary>
public sealed class TaskRunnerTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时工作目录（名称带随机 GUID）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-task-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建本测试实例使用的临时工作目录。
    /// </summary>
    public TaskRunnerTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：命令同时写标准输出与标准错误并以退出码 7 结束时，结果如实记录退出码 7，合并输出同时包含两路文本，
    /// 标准输出也通过回调实时转发；结果标记为失败但未取消，结束时间不早于开始时间。
    /// </summary>
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

    /// <summary>
    /// 验证：命令在指定的、名称含空格的工作目录中运行——重定向写出的文件出现在该目录中，而不在其上级目录中。
    /// </summary>
    [Fact]
    public async Task UsesWorkingDirectoryIncludingSpaces()
    {
        var working = Directory.CreateDirectory(Path.Combine(root, "a directory")).FullName;
        var result = await new TaskRunner().RunAsync(new("cwd", "echo actual > output.txt", working));
        Assert.True(result.Succeeded);
        Assert.Contains("actual", File.ReadAllText(Path.Combine(working, "output.txt")));
        Assert.False(File.Exists(Path.Combine(root, "output.txt")));
    }

    /// <summary>
    /// 验证：等命令输出 <c>started</c>（确认已在运行）后取消，原本要运行约 60 秒的命令在 5 秒内被终止，
    /// 返回的结果标记为已取消且不成功。
    /// </summary>
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

    /// <summary>
    /// 验证：并发上限为 1 时，在第一条命令运行期间提交的第二条命令会排队，直到第一条结束后才开始，两条命令都成功。
    /// </summary>
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

    /// <summary>
    /// 验证：输出上限为 100 个字符时，输出 200 行的命令仍然成功，结果标记为已截断，保留的输出不超过 100 个字符且包含最后一行的 <c>200</c>。
    /// </summary>
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

    /// <summary>
    /// 验证：传入已取消的令牌时命令不会被执行（未创建标记文件），结果标记为已取消且退出码为 <see langword="null"/>。
    /// </summary>
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

    /// <summary>
    /// 验证：工作目录不存在时，<see cref="TaskRunner.RunAsync"/> 在启动进程前抛出 <see cref="DirectoryNotFoundException"/>。
    /// </summary>
    [Fact]
    public async Task MissingWorkingDirectoryIsRejectedBeforeLaunch()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new TaskRunner().RunAsync(new("bad", "echo bad", Path.Combine(root, "missing"))));
    }

    /// <summary>
    /// 验证：命令只含空白时，<see cref="TaskRunner.RunAsync"/> 抛出 <see cref="ArgumentException"/>。
    /// </summary>
    [Fact]
    public async Task EmptyCommandIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskRunner().RunAsync(new("bad", " ", root)));
    }

    /// <summary>
    /// 验证（Windows）：带引号的、路径含空格的 <c>.cmd</c> 脚本与带引号的含空格参数原样传给 Shell，
    /// 脚本收到完整参数 <c>a b</c> 并以退出码 0 结束。
    /// </summary>
    [WindowsFact]
    public async Task QuotedExecutableAndArgumentWithSpacesRunUnchanged()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "commands with spaces")).FullName;
        var script = Path.Combine(directory, "show argument.cmd");
        File.WriteAllText(script, "@echo off\r\necho [%~1]\r\n", System.Text.Encoding.ASCII);
        var result = await new TaskRunner().RunAsync(new("quoted", $"\"{script}\" \"a b\"", root));
        Assert.Contains("[a b]", result.Output);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// 验证：Shell 输出的中文文本在结果中保持可读，没有因代码页解码而变成乱码。
    /// </summary>
    [Fact]
    public async Task ChineseShellOutputRemainsReadable()
    {
        var result = await new TaskRunner().RunAsync(new("unicode", "echo 你好终端", root));
        Assert.True(result.Succeeded);
        Assert.Contains("你好终端", result.Output);
    }

    /// <summary>
    /// 验证：引号内的 <c>&amp;</c>、<c>!</c>、<c>%</c> 与中文作为一个字面参数传给 PowerShell 脚本（脚本以 JSON 记录收到的参数），
    /// 而引号外的 <c>&amp;&amp;</c> 仍作为 Shell 运算符执行后续命令，后续命令的中文输出也出现在结果中。
    /// </summary>
    [Fact]
    public async Task QuotedMetacharactersStayLiteralWhileShellOperatorsAndChineseStillWork()
    {
        var script = Path.Combine(root, "literal arguments.ps1");
        PowerShellFixture.WriteScript(script, "[IO.File]::WriteAllText('arguments.json', (ConvertTo-Json -InputObject @($args) -Compress))");
        var command = $"{PowerShellFixture.CommandLine(script)} \"中文 & literal! 100%\" && echo 后续";

        var result = await new TaskRunner().RunAsync(new("literal", command, root));

        Assert.True(result.Succeeded, result.Output);
        var arguments = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "arguments.json")));
        Assert.Equal(new[] { "中文 & literal! 100%" }, arguments);
        Assert.Contains("后续", result.Output);
    }

    /// <summary>
    /// 验证：并发上限或输出字符上限为 0 时，<see cref="TaskRunner"/> 构造函数抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <param name="concurrency">同时运行的命令上限。</param>
    /// <param name="output">每次运行保留的输出字符上限。</param>
    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 0)]
    public void InvalidLimitsAreRejected(int concurrency, int output)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TaskRunner(concurrency, output));

    /// <summary>
    /// 递归删除本测试实例的临时工作目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
