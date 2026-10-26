namespace CrewTerm.Automation;

public sealed record PipelineStepTemplate(string Name, string Command);

public sealed record PipelineTemplate(string Id, string Name, IReadOnlyList<PipelineStepTemplate> Steps)
{
    public IReadOnlyList<CommandTask> ForProject(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var path = Path.GetFullPath(workingDirectory);
        return Steps.Select(step => new CommandTask(step.Name, step.Command, path)).ToArray();
    }
}
