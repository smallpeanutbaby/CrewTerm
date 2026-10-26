namespace CrewTerm.Automation;

public sealed class PipelineTemplateStore
{
    private readonly string path;

    public PipelineTemplateStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        path = Path.Combine(Path.GetFullPath(stateDirectory), "pipeline-templates.json");
        _ = List();
    }

    public IReadOnlyList<PipelineTemplate> List()
    {
        lock (AtomicJsonFile.Gate(path)) return Load().ToArray();
    }

    public void Upsert(PipelineTemplate template)
    {
        Validate(template);
        lock (AtomicJsonFile.Gate(path))
        {
            var rows = Load().Where(t => !t.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase)).Append(template).ToArray();
            AtomicJsonFile.Write(path, rows);
        }
    }

    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (AtomicJsonFile.Gate(path))
        {
            var rows = Load();
            var removed = rows.RemoveAll(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) AtomicJsonFile.Write(path, rows);
            return removed;
        }
    }

    private List<PipelineTemplate> Load()
    {
        var templates = AtomicJsonFile.Read(path, () => new List<PipelineTemplate>());
        try { foreach (var template in templates) Validate(template); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid pipeline templates; original file preserved: {path}", error);
        }
        return templates;
    }

    private static void Validate(PipelineTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        ArgumentNullException.ThrowIfNull(template.Steps);
        if (template.Steps.Count == 0) throw new ArgumentException("A pipeline must contain at least one step.", nameof(template));
        foreach (var step in template.Steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Command);
        }
    }
}
