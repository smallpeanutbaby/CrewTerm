namespace CrewTerm.Core;

public sealed record ProjectEntry(string Id, string Name, string Path);

public sealed class ProjectCatalog
{
    private readonly string file;
    private readonly List<ProjectEntry> projects;
    private string? fingerprint;
    public IReadOnlyList<ProjectEntry> Projects => projects.AsReadOnly();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public ProjectCatalog(string stateDirectory)
    {
        file = System.IO.Path.Combine(System.IO.Path.GetFullPath(stateDirectory), "projects.json");
        projects = AtomicJsonStore.Load<List<ProjectEntry>>(file, out fingerprint) ?? [];
        if (projects.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Path)))
            throw new InvalidDataException($"Invalid project settings; original file preserved: {file}");
    }

    public ProjectEntry Add(string path, string? name = null)
    {
        var normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException(normalized);
        var existing = projects.Find(p => PathComparer.Equals(p.Path, normalized));
        if (existing is not null) return existing;
        var entry = new ProjectEntry(Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(normalized).Name : name, normalized);
        projects.Add(entry);
        return entry;
    }

    public void Remove(string id) => projects.RemoveAll(p => p.Id == id);
    public void Save() => fingerprint = AtomicJsonStore.SaveIfUnchanged(file, projects, fingerprint);
}
