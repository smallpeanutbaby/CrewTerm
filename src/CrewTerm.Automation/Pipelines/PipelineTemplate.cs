using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Pipelines;

/// <summary>
/// 可复用的流水线模板；不绑定项目，运行时再指定工作目录。
/// </summary>
/// <param name="Id">模板标识。</param>
/// <param name="Name">模板名称。</param>
/// <param name="Steps">按执行顺序排列的步骤。</param>
public sealed record PipelineTemplate(string Id, string Name, IReadOnlyList<PipelineStepTemplate> Steps)
{
    /// <summary>
    /// 把模板实例化为在指定项目目录中执行的命令序列。
    /// </summary>
    /// <param name="workingDirectory">项目目录。</param>
    /// <returns>每个步骤对应的 <see cref="CommandTask"/>。</returns>
    public IReadOnlyList<CommandTask> ForProject(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var path = Path.GetFullPath(workingDirectory);
        return Steps.Select(step => new CommandTask(step.Name, step.Command, path)).ToArray();
    }
}
