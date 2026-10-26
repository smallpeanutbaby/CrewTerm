namespace CrewTerm.Core;

public sealed record CommandTemplate(string Name, string Command, string WorkingDirectory);

public sealed class WorkspaceSettings
{
    public string? LastProjectId { get; set; }
    public string? LastSessionId { get; set; }
    public bool SplitTerminals { get; set; }
    public int MaximumConcurrentTasks { get; set; } = 4;
    public List<CommandTemplate> Templates { get; set; } = [];
}

public sealed class WorkspaceSettingsStore(string stateDirectory)
{
    private readonly string file = Path.Combine(Path.GetFullPath(stateDirectory), "workspace.json");
    private string? fingerprint;
    public WorkspaceSettings Load()
    {
        var settings = AtomicJsonStore.Load<WorkspaceSettings>(file, out fingerprint) ?? new();
        Validate(settings);
        return settings;
    }
    public void Save(WorkspaceSettings settings)
    {
        Validate(settings);
        fingerprint = AtomicJsonStore.SaveIfUnchanged(file, settings, fingerprint);
    }
    private static void Validate(WorkspaceSettings settings)
    {
        if (settings.MaximumConcurrentTasks is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(settings), "Concurrency must be between 1 and 32");
        if (settings.Templates is null) throw new InvalidDataException("Templates must be an array");
    }
}
