namespace CrewTerm.Automation;

public sealed class TaskHistory
{
    private readonly string path;
    private readonly int maxEntries;

    public TaskHistory(string stateDirectory, int maxEntries = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        Directory.CreateDirectory(stateDirectory);
        path = Path.Combine(Path.GetFullPath(stateDirectory), "task-history.json");
        this.maxEntries = maxEntries;
        _ = List();
    }

    public IReadOnlyList<TaskRunResult> List()
    {
        lock (AtomicJsonFile.Gate(path))
        {
            return Load().OrderByDescending(r => r.StartedAt).Take(maxEntries).ToArray();
        }
    }

    public void Add(TaskRunResult result)
    {
        Validate(result);
        lock (AtomicJsonFile.Gate(path))
        {
            var rows = Load().Where(r => r.Id != result.Id).Append(result)
                .OrderByDescending(r => r.StartedAt).Take(maxEntries).ToArray();
            AtomicJsonFile.Write(path, rows);
        }
    }

    private List<TaskRunResult> Load()
    {
        var rows = AtomicJsonFile.Read(path, () => new List<TaskRunResult>());
        try { foreach (var row in rows) Validate(row); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid task history; original file preserved: {path}", error);
        }
        return rows;
    }

    private static void Validate(TaskRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(result.Output);
        if (result.FinishedAt < result.StartedAt)
            throw new ArgumentException("A run cannot finish before it starts.", nameof(result));
    }
}
