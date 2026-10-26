using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrewTerm.Workspace;

public sealed record CheckpointInfo(string Id, string Label, string Root, DateTimeOffset CreatedAt, int FileCount, long TotalBytes);
public sealed record CheckpointChange(string RelativePath, string Kind, string BeforeText, string AfterText);

public sealed class CheckpointPreview
{
    public string Root { get; }
    public CheckpointInfo Checkpoint { get; }
    public IReadOnlyList<CheckpointChange> Changes { get; }
    internal IReadOnlyDictionary<string, byte[]> TargetFiles { get; }
    internal IReadOnlyDictionary<string, string> ExpectedHashes { get; }

    internal CheckpointPreview(string root, CheckpointInfo checkpoint, IReadOnlyList<CheckpointChange> changes,
        IReadOnlyDictionary<string, byte[]> targetFiles, IReadOnlyDictionary<string, string> expectedHashes)
    {
        Root = root; Checkpoint = checkpoint; Changes = changes;
        TargetFiles = targetFiles; ExpectedHashes = expectedHashes;
    }
}

public sealed class CheckpointStore
{
    public const int MaximumFiles = 4000;
    public const long MaximumTotalBytes = 64L * 1024 * 1024;
    private readonly string _stateDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public CheckpointStore(string stateDirectory) => _stateDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(stateDirectory, "checkpoints"));

    public Task<CheckpointInfo> CaptureAsync(string root, string label, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = WorkspacePath.Root(root);
            EnsureExternalStorage(root);
            ArgumentException.ThrowIfNullOrWhiteSpace(label);
            var files = ReadProjectFiles(root, cancellationToken);
            var id = Guid.NewGuid().ToString("N");
            var info = new CheckpointInfo(id, label.Trim(), root, DateTimeOffset.UtcNow, files.Count, files.Values.Sum(content => (long)content.Length));
            var folder = ProjectFolder(root);
            Directory.CreateDirectory(folder);
            WriteJsonAtomically(System.IO.Path.Combine(folder, id + ".snapshot.json"), new StoredCheckpoint(info, files));
            WriteJsonAtomically(System.IO.Path.Combine(folder, id + ".meta.json"), info);
            return info;
        }, cancellationToken);

    public IReadOnlyList<CheckpointInfo> List(string root)
    {
        root = WorkspacePath.Root(root);
        var folder = ProjectFolder(root);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.meta.json")
            .Select(path => JsonSerializer.Deserialize<CheckpointInfo>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Invalid checkpoint metadata: {path}"))
            .Where(info => info.Root.Equals(root, WorkspacePath.Comparison))
            .OrderByDescending(info => info.CreatedAt).ToArray();
    }

    public Task<CheckpointPreview> PreviewAsync(string root, string id, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = WorkspacePath.Root(root);
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid checkpoint identifier.", nameof(id));
            var snapshotPath = System.IO.Path.Combine(ProjectFolder(root), id + ".snapshot.json");
            if (new FileInfo(snapshotPath).Length > MaximumTotalBytes * 2) throw new InvalidDataException("Checkpoint exceeds the storage size limit.");
            var stored = JsonSerializer.Deserialize<StoredCheckpoint>(File.ReadAllText(snapshotPath)) ?? throw new InvalidDataException("Invalid checkpoint.");
            if (!stored.Info.Root.Equals(root, WorkspacePath.Comparison) || stored.Info.Id != id) throw new InvalidDataException("Checkpoint belongs to a different project.");
            ValidateLimits(stored.Files);
            foreach (var relative in stored.Files.Keys) WorkspacePath.Resolve(root, relative, allowMissing: true);
            var current = ReadProjectFiles(root, cancellationToken);
            var changes = new List<CheckpointChange>();
            foreach (var relative in current.Keys.Union(stored.Files.Keys, StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))
            {
                current.TryGetValue(relative, out var before);
                stored.Files.TryGetValue(relative, out var after);
                if (before is not null && after is not null && before.AsSpan().SequenceEqual(after)) continue;
                changes.Add(new CheckpointChange(relative, before is null ? "Missing" : after is null ? "Added" : "Modified", PreviewText(before), PreviewText(after)));
            }
            return new CheckpointPreview(root, stored.Info, changes, stored.Files,
                current.ToDictionary(pair => pair.Key, pair => Hash(pair.Value), StringComparer.Ordinal));
        }, cancellationToken);

    public Task RestoreAsync(CheckpointPreview preview, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            cancellationToken.ThrowIfCancellationRequested();
            var root = WorkspacePath.Root(preview.Root);
            var current = ReadProjectFiles(root, cancellationToken);
            if (current.Count != preview.ExpectedHashes.Count || current.Any(pair => !preview.ExpectedHashes.TryGetValue(pair.Key, out var hash) || hash != Hash(pair.Value)))
                throw new FileConflictException(root);

            // Preflight every path before modifying any file. Restore is explicit: files
            // listed as Added in the reviewed preview will be removed.
            foreach (var change in preview.Changes) WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var change in preview.Changes)
            {
                var path = WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
                var expected = preview.ExpectedHashes.GetValueOrDefault(change.RelativePath);
                if ((File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null) != expected)
                    throw new FileConflictException(path);
                if (!preview.TargetFiles.TryGetValue(change.RelativePath, out var content))
                {
                    File.Delete(path);
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".crewterm-{Guid.NewGuid():N}.tmp");
                try
                {
                    WriteDurable(temporary, content);
                    WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
                    if ((File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null) != expected) throw new FileConflictException(path);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }, cancellationToken);

    private static Dictionary<string, byte[]> ReadProjectFiles(string root, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                if ((item.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!FileWorkspace.ExcludedDirectories.Contains(item.Name)) pending.Push(item.FullName);
                    continue;
                }
                if (files.Count >= MaximumFiles) throw new InvalidDataException($"Checkpoint exceeds {MaximumFiles} source files.");
                if (((FileInfo)item).Length > FileWorkspace.MaximumFileBytes) throw new InvalidDataException($"Checkpoint file is larger than 4 MiB: {item.FullName}");
                var content = File.ReadAllBytes(item.FullName);
                total += content.Length;
                if (total > MaximumTotalBytes) throw new InvalidDataException("Checkpoint exceeds the 64 MiB source-file limit.");
                files.Add(WorkspacePath.Relative(root, item.FullName), content);
            }
        }
        return files;
    }

    private void EnsureExternalStorage(string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, _stateDirectory);
        if (relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            throw new InvalidOperationException("Checkpoint storage must be outside the selected project.");
    }

    private string ProjectFolder(string root)
    {
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return System.IO.Path.Combine(_stateDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))));
    }

    private static void ValidateLimits(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count > MaximumFiles || files.Any(file => file.Value.Length > FileWorkspace.MaximumFileBytes) || files.Values.Sum(content => (long)content.Length) > MaximumTotalBytes)
            throw new InvalidDataException("Checkpoint exceeds supported limits.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string PreviewText(byte[]? bytes)
    {
        if (bytes is null) return "";
        if (bytes.Contains((byte)0)) return $"[Binary file: {bytes.Length} bytes]";
        var text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 8192)));
        return bytes.Length > 8192 ? text + "\n[Preview truncated]" : text;
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteDurable(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private sealed record StoredCheckpoint(CheckpointInfo Info, Dictionary<string, byte[]> Files);
}
