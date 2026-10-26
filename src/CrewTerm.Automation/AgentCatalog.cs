namespace CrewTerm.Automation;

public sealed class AgentCatalog
{
    private readonly string? path;
    private readonly object gate;
    private List<AgentProfile> memoryProfiles = [];

    public AgentCatalog(string? stateDirectory = null)
    {
        if (stateDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
            Directory.CreateDirectory(stateDirectory);
            path = Path.Combine(Path.GetFullPath(stateDirectory), "agent-profiles.json");
        }
        gate = path is null ? new object() : AtomicJsonFile.Gate(path);
        _ = Profiles;
    }

    public IReadOnlyList<AgentProfile> Profiles
    {
        get
        {
            lock (gate)
            {
                var profiles = BuiltIns().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var custom in Load()) profiles[custom.Id] = custom with { IsBuiltIn = false };
                return profiles.Values.ToArray();
            }
        }
    }

    public IReadOnlyList<AgentAvailability> Discover()
        => Profiles.Select(profile => new AgentAvailability(profile, ExecutableLocator.Find(profile.Executable))).ToArray();

    public void Upsert(AgentProfile profile)
    {
        Validate(profile);
        lock (gate)
        {
            var profiles = Load().Where(p => !p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            profiles.Add(profile with { IsBuiltIn = false });
            Save(profiles);
        }
    }

    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            var profiles = Load();
            var removed = profiles.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save(profiles);
            return removed;
        }
    }

    private List<AgentProfile> Load()
    {
        var profiles = path is null ? memoryProfiles.ToList() : AtomicJsonFile.Read(path, () => new List<AgentProfile>());
        try { foreach (var profile in profiles) Validate(profile); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid agent profile data; original file preserved: {path}", error);
        }
        return profiles;
    }

    private void Save(List<AgentProfile> profiles)
    {
        if (path is not null) AtomicJsonFile.Write(path, profiles);
        else memoryProfiles = profiles;
    }

    private static void Validate(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Executable);
        if (profile.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Profile IDs may contain letters, digits, hyphens and underscores.", nameof(profile));
        if (profile.Executable.Contains('"') || profile.Executable.Contains('\n') || profile.Executable.Contains('\r'))
            throw new ArgumentException("The executable field must contain an unquoted executable name or path.", nameof(profile));
    }

    private static IEnumerable<AgentProfile> BuiltIns()
    {
        yield return new("claude", "Claude Code", "claude", IsBuiltIn: true);
        yield return new("codex", "Codex", "codex", IsBuiltIn: true);
        yield return new("opencode", "OpenCode", "opencode", IsBuiltIn: true);
        yield return new("gemini", "Gemini CLI", "gemini", IsBuiltIn: true);
        yield return new("copilot", "GitHub Copilot CLI", "copilot", IsBuiltIn: true);
        var shell = OperatingSystem.IsWindows()
            ? ExecutableLocator.Find("pwsh") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        yield return new("shell", "Shell", shell, IsBuiltIn: true);
    }
}
