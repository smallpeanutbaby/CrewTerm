using CrewTerm.Automation.Pipelines;

namespace CrewTerm.Automation.Tests.Pipelines;

/// <summary>
/// 覆盖 <see cref="PipelineRunner"/> 按顺序执行多个命令步骤的行为：失败即停止、成功步骤按依赖顺序执行、预先取消时什么都不执行。
/// 各步骤通过系统 Shell 以真实进程运行，在系统临时目录下独立的工作目录中读写文件，测试结束后整体删除。
/// </summary>
public sealed class PipelineRunnerTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时工作目录（名称带随机 GUID）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-pipeline-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时工作目录。
    /// </summary>
    public PipelineRunnerTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：第一步以退出码 9 失败后流水线立即停止，结果只含这一步并标记为失败，后一步没有执行（未创建文件）。
    /// </summary>
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

    /// <summary>
    /// 验证：两个成功步骤按顺序执行——第二步能读到第一步写出的文件内容 <c>handoff</c>，且第一步的结束时间不晚于第二步的开始时间。
    /// </summary>
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

    /// <summary>
    /// 验证：传入已取消的令牌时，流水线结果标记为已取消且不成功，步骤命令没有执行（未创建标记文件）。
    /// </summary>
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

    /// <summary>
    /// 递归删除本测试实例的临时工作目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
