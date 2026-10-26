using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Pipelines;

/// <summary>
/// 按顺序执行用户明确配置的命令序列；任何一步失败或被取消后立即停止，后续步骤不会运行。
/// </summary>
/// <param name="runner">执行单个步骤的运行器；为 <see langword="null"/> 时创建默认运行器。</param>
public sealed class PipelineRunner(TaskRunner? runner = null)
{
    /// <summary>
    /// 实际执行每个步骤的运行器；其并发上限与输出上限对流水线的每一步同样生效。
    /// </summary>
    private readonly TaskRunner runner = runner ?? new TaskRunner();

    /// <summary>
    /// 运行流水线。
    /// </summary>
    /// <param name="tasks">按执行顺序排列的步骤。</param>
    /// <param name="onOutput">实时输出回调，所有步骤共用。</param>
    /// <param name="cancellationToken">取消当前步骤并跳过其余步骤。</param>
    /// <returns>流水线结果。</returns>
    public async Task<PipelineResult> RunAsync(IEnumerable<CommandTask> tasks,
        Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var results = new List<TaskRunResult>();
        foreach (var task in tasks)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var result = await runner.RunAsync(task, onOutput, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Succeeded) break;
        }
        return new(results.ToArray(), cancellationToken.IsCancellationRequested || results.Any(r => r.Cancelled));
    }
}
