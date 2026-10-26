using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrewTerm.Core.IO;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Files;

namespace CrewTerm.Workspace.Checkpoints;

/// <summary>
/// 项目文本文件的本地检查点：捕获、列出、预览差异与经确认后的恢复。
/// </summary>
/// <remarks>
/// <para>检查点保存在状态目录的 <see cref="StateFiles.Checkpoints"/> 子目录中（必须位于项目之外），
/// 每个项目一个以路径哈希命名的文件夹；每个检查点由 <c>{id}.snapshot.json</c>（全部内容）
/// 和 <c>{id}.meta.json</c>（元数据，供快速列出）组成。</para>
/// <para>上限：<see cref="MaximumFiles"/> 个文件、总计 <see cref="MaximumTotalBytes"/> 字节、
/// 单文件 <see cref="FileWorkspace.MaximumFileBytes"/> 字节。恢复逐文件原子替换并逐文件检查冲突，
/// 但不是跨文件的事务：中途失败时已恢复的文件不会回滚。</para>
/// </remarks>
public sealed class CheckpointStore
{
    /// <summary>
    /// 单个检查点最多包含的文件数。
    /// </summary>
    public const int MaximumFiles = 4000;

    /// <summary>
    /// 单个检查点的文件总字节上限（64 MiB）。
    /// </summary>
    public const long MaximumTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 预览中每个文件最多显示的字节数。
    /// </summary>
    private const int PreviewBytes = 8192;

    /// <summary>
    /// 检查点根目录的绝对路径（状态目录下的 <see cref="StateFiles.Checkpoints"/> 子目录）。
    /// 名称沿用旧称，实际不是状态目录本身；每个项目在其下占用一个哈希命名的文件夹。
    /// </summary>
    private readonly string _stateDirectory;

    /// <summary>
    /// 检查点 JSON 的序列化选项：不缩进，以减小快照体积（文件内容以 Base64 存储，本身就很大）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// 创建检查点存储。
    /// </summary>
    /// <param name="stateDirectory">工作台状态目录；检查点保存在其 <see cref="StateFiles.Checkpoints"/> 子目录。</param>
    public CheckpointStore(string stateDirectory) => _stateDirectory = StateFiles.PathOf(stateDirectory, StateFiles.Checkpoints);

    /// <summary>
    /// 捕获项目当前的全部文件（跳过 <c>.git</c>、构建产物目录与链接）。
    /// </summary>
    /// <remarks>
    /// 在线程池上执行。二进制文件同样按原始字节保存；先写快照再写元数据，两者都以“只创建、不覆盖”的方式原子写入。
    /// </remarks>
    /// <param name="root">项目根目录。</param>
    /// <param name="label">检查点名称，保存时去掉首尾空白。</param>
    /// <param name="cancellationToken">取消捕获。</param>
    /// <returns>新检查点的元数据。</returns>
    /// <exception cref="InvalidOperationException">状态目录位于项目内部（检查点会把自己也捕获进去）。</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> 为空白。</exception>
    /// <exception cref="InvalidDataException">超出文件数或大小上限。</exception>
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
            // 先写快照再写元数据：List 只读取元数据，因此不会列出缺少快照的检查点。
            WriteJsonAtomically(System.IO.Path.Combine(folder, id + ".snapshot.json"), new StoredCheckpoint(info, files));
            WriteJsonAtomically(System.IO.Path.Combine(folder, id + ".meta.json"), info);
            return info;
        }, cancellationToken);

    /// <summary>
    /// 列出项目的检查点，最新的在前。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <returns>检查点元数据列表。</returns>
    public IReadOnlyList<CheckpointInfo> List(string root)
    {
        root = WorkspacePath.Root(root);
        var folder = ProjectFolder(root);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.meta.json")
            .Select(path => JsonSerializer.Deserialize<CheckpointInfo>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Invalid checkpoint metadata: {path}"))
            .Where(info => FileSystemPath.AreSame(info.Root, root))
            .OrderByDescending(info => info.CreatedAt).ToArray();
    }

    /// <summary>
    /// 比较检查点与当前磁盘内容，生成恢复预览（不修改任何文件）。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="id">检查点标识。</param>
    /// <param name="cancellationToken">取消比较。</param>
    /// <returns>恢复预览。</returns>
    /// <exception cref="InvalidDataException">检查点损坏、超限或属于其他项目。</exception>
    public Task<CheckpointPreview> PreviewAsync(string root, string id, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = WorkspacePath.Root(root);
            // 标识必须是 32 位十六进制 GUID，防止通过 id 拼出项目文件夹之外的路径。
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid checkpoint identifier.", nameof(id));
            var snapshotPath = System.IO.Path.Combine(ProjectFolder(root), id + ".snapshot.json");
            // Base64 编码约使体积增加 1/3，留出 2 倍余量后仍拒绝明显异常的大文件。
            if (new FileInfo(snapshotPath).Length > MaximumTotalBytes * 2) throw new InvalidDataException("Checkpoint exceeds the storage size limit.");
            var stored = JsonSerializer.Deserialize<StoredCheckpoint>(File.ReadAllText(snapshotPath)) ?? throw new InvalidDataException("Invalid checkpoint.");
            if (!FileSystemPath.AreSame(stored.Info.Root, root) || stored.Info.Id != id) throw new InvalidDataException("Checkpoint belongs to a different project.");
            ValidateLimits(stored.Files);
            // 快照文件可能被篡改：每个相对路径都必须仍能安全解析到项目内部。
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

    /// <summary>
    /// 按用户已确认的预览执行恢复。
    /// </summary>
    /// <param name="preview">由 <see cref="PreviewAsync"/> 生成并已展示给用户的预览。</param>
    /// <param name="cancellationToken">在修改任何文件之前取消。</param>
    /// <exception cref="FileConflictException">预览之后项目文件又发生了变化。</exception>
    public Task RestoreAsync(CheckpointPreview preview, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            cancellationToken.ThrowIfCancellationRequested();
            var root = WorkspacePath.Root(preview.Root);
            var current = ReadProjectFiles(root, cancellationToken);
            if (current.Count != preview.ExpectedHashes.Count || current.Any(pair => !preview.ExpectedHashes.TryGetValue(pair.Key, out var hash) || hash != Hash(pair.Value)))
                throw new FileConflictException(root);

            // 修改任何文件之前先校验全部路径。恢复是用户明确确认的操作：
            // 预览中标记为 Added 的文件会被删除。
            foreach (var change in preview.Changes) WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var change in preview.Changes)
            {
                var path = WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
                var expected = preview.ExpectedHashes.GetValueOrDefault(change.RelativePath);
                if (CurrentHash(path) != expected)
                    throw new FileConflictException(path);
                if (!preview.TargetFiles.TryGetValue(change.RelativePath, out var content))
                {
                    File.Delete(path);
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                // 替换前再次校验路径与内容，防止写临时文件期间被 Agent 修改或换成链接。
                AtomicFile.WriteAllBytes(path, content, beforeCommit: () =>
                {
                    WorkspacePath.Resolve(root, change.RelativePath, allowMissing: true);
                    if (CurrentHash(path) != expected) throw new FileConflictException(path);
                });
            }
        }, cancellationToken);

    /// <summary>
    /// 读取项目内全部文件内容（相对路径 → 字节），超出上限时抛出异常。
    /// </summary>
    /// <remarks>
    /// 用显式栈做深度优先遍历；跳过符号链接与目录联接、<c>.git</c> 以及 <see cref="FileWorkspace.ExcludedDirectories"/> 中的目录。
    /// 每处理一项都检查取消。
    /// </remarks>
    /// <param name="root">已规范化的项目根目录。</param>
    /// <param name="cancellationToken">取消遍历。</param>
    /// <returns>以 <c>/</c> 分隔的相对路径（区分大小写）到文件字节的映射。</returns>
    /// <exception cref="InvalidDataException">文件数超过 <see cref="MaximumFiles"/>、单个文件超过 4 MiB 或总量超过 <see cref="MaximumTotalBytes"/>。</exception>
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

    /// <summary>
    /// 拒绝把检查点存放在被捕获的项目内部。
    /// </summary>
    /// <remarks>
    /// 从项目根到检查点目录的相对路径必须以 <c>..</c> 开头或是另一个卷上的绝对路径，否则检查点目录位于项目之内。
    /// </remarks>
    /// <param name="root">已规范化的项目根目录。</param>
    /// <exception cref="InvalidOperationException">检查点目录位于项目内部（或就是项目根目录）。</exception>
    private void EnsureExternalStorage(string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, _stateDirectory);
        if (relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            throw new InvalidOperationException("Checkpoint storage must be outside the selected project.");
    }

    /// <summary>
    /// 项目对应的检查点文件夹：规范化根路径（Windows 下转大写）的 SHA-256。
    /// </summary>
    /// <param name="root">已规范化的项目根目录。</param>
    /// <returns>检查点根目录下以哈希（64 个大写十六进制字符）命名的文件夹路径（不保证已存在）。</returns>
    private string ProjectFolder(string root)
    {
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return System.IO.Path.Combine(_stateDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))));
    }

    /// <summary>
    /// 校验从磁盘读回的快照仍在文件数与大小上限之内（快照文件可能被手工修改）。
    /// </summary>
    /// <param name="files">快照中的文件内容。</param>
    /// <exception cref="InvalidDataException">超出上限。</exception>
    private static void ValidateLimits(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count > MaximumFiles || files.Any(file => file.Value.Length > FileWorkspace.MaximumFileBytes) || files.Values.Sum(content => (long)content.Length) > MaximumTotalBytes)
            throw new InvalidDataException("Checkpoint exceeds supported limits.");
    }

    /// <summary>
    /// 计算文件内容的 SHA-256。
    /// </summary>
    /// <param name="bytes">文件内容。</param>
    /// <returns>大写十六进制哈希。</returns>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// 磁盘上文件当前内容的哈希；文件不存在时为 <see langword="null"/>。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    /// <returns>大写十六进制 SHA-256，或文件不存在时的 <see langword="null"/>。</returns>
    private static string? CurrentHash(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

    /// <summary>
    /// 生成用于预览的文本：二进制文件只显示大小，文本最多显示前 8 KiB。
    /// </summary>
    /// <param name="bytes">文件内容；文件不存在时为 <see langword="null"/>。</param>
    /// <returns>预览文本；文件不存在时为空字符串。</returns>
    private static string PreviewText(byte[]? bytes)
    {
        if (bytes is null) return "";
        if (bytes.Contains((byte)0)) return $"[Binary file: {bytes.Length} bytes]";
        var text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, PreviewBytes)));
        return bytes.Length > PreviewBytes ? text + "\n[Preview truncated]" : text;
    }

    /// <summary>
    /// 原子写入新的 JSON 文件；目标已存在时失败（检查点文件只写一次）。
    /// </summary>
    /// <typeparam name="T">序列化类型。</typeparam>
    /// <param name="path">目标文件路径，所在目录必须已存在。</param>
    /// <param name="value">要序列化的值。</param>
    /// <exception cref="IOException">目标已存在或写入失败。</exception>
    private static void WriteJsonAtomically<T>(string path, T value)
        => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), overwrite: false);
}
