using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Pipelines;

/// <summary>
/// 一次流水线运行的结果。
/// </summary>
/// <param name="Steps">实际执行过的步骤结果（失败或取消后的步骤不会出现）。</param>
/// <param name="Cancelled">是否被取消。</param>
public sealed record PipelineResult(IReadOnlyList<TaskRunResult> Steps, bool Cancelled)
{
    /// <summary>
    /// 未取消、至少执行了一个步骤，且所有步骤都以退出码 0 结束。
    /// </summary>
    public bool Succeeded => !Cancelled && Steps.Count > 0 && Steps.All(step => step.Succeeded);
}
