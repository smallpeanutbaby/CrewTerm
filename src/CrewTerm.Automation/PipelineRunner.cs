namespace CrewTerm.Automation;

public sealed record PipelineResult(IReadOnlyList<TaskRunResult> Steps, bool Cancelled)
{
    public bool Succeeded => !Cancelled && Steps.Count > 0 && Steps.All(step => step.Succeeded);
}

/// <summary>Executes an explicitly configured sequence; later steps require successful prior steps.</summary>
public sealed class PipelineRunner(TaskRunner? runner = null)
{
    private readonly TaskRunner runner = runner ?? new TaskRunner();

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
