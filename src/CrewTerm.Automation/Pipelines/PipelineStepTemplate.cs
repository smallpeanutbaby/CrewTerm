namespace CrewTerm.Automation.Pipelines;

/// <summary>
/// 流水线模板中的一个步骤。
/// </summary>
/// <param name="Name">步骤名称。</param>
/// <param name="Command">交给系统 Shell 执行的命令文本。</param>
public sealed record PipelineStepTemplate(string Name, string Command);
