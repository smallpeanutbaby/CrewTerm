namespace CrewTerm.Automation;

/// <summary>A noninteractive shell command deliberately supplied by the user.</summary>
public sealed record CommandTask(string Name, string Command, string WorkingDirectory);
