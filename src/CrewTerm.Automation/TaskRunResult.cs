namespace CrewTerm.Automation;

public sealed record TaskRunResult(
    string Id,
    string Name,
    string Command,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? ExitCode,
    string Output,
    bool Cancelled,
    bool OutputTruncated = false)
{
    public bool Succeeded => !Cancelled && ExitCode == 0;
    public TimeSpan Duration => FinishedAt - StartedAt;
}
