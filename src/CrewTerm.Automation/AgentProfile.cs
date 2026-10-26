namespace CrewTerm.Automation;

public sealed record AgentProfile(string Id, string Name, string Executable, string Arguments = "", bool IsBuiltIn = false)
{
    public string CommandLine
    {
        get
        {
            var executable = Executable.IndexOfAny([' ', '\t', '&', '(', ')', '^']) >= 0
                ? $"\"{Executable}\"" : Executable;
            return string.IsNullOrWhiteSpace(Arguments) ? executable : executable + " " + Arguments;
        }
    }
}

public sealed record AgentAvailability(AgentProfile Profile, string? ExecutablePath)
{
    public bool IsAvailable => ExecutablePath is not null;
}
