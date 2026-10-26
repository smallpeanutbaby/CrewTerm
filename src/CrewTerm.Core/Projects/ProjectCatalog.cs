using CrewTerm.Core.IO;

namespace CrewTerm.Core.Projects;

/// <summary>
/// 持久化的项目列表（<see cref="StateFiles.Projects"/>）。
/// </summary>
/// <remarks>
/// 同一路径（按 <see cref="FileSystemPath"/> 规则比较）只会登记一次。修改在调用
/// <see cref="Save"/> 前只存在于内存；保存采用乐观并发，若其他客户端已修改文件则拒绝覆盖。
/// 从列表移除项目不会删除磁盘上的目录。本类不是线程安全的，应只在 UI 线程使用。
/// </remarks>
public sealed class ProjectCatalog
{
    /// <summary>
    /// 项目列表文件的完整路径（状态目录下的 <see cref="StateFiles.Projects"/>）。
    /// </summary>
    private readonly string file;

    /// <summary>
    /// 内存中的项目列表，按登记顺序排列；<see cref="Save"/> 之前的修改只存在于这里。
    /// </summary>
    private readonly List<ProjectEntry> projects;

    /// <summary>
    /// 最近一次加载或保存时文件内容的 SHA-256 指纹，用于乐观并发检查；加载时文件不存在则为 <see langword="null"/>。
    /// </summary>
    private string? fingerprint;

    /// <summary>
    /// 当前项目列表（只读视图）。
    /// </summary>
    public IReadOnlyList<ProjectEntry> Projects => projects.AsReadOnly();

    /// <summary>
    /// 从状态目录加载项目列表；文件不存在时为空列表。
    /// </summary>
    /// <param name="stateDirectory">状态目录。</param>
    /// <exception cref="InvalidDataException">文件损坏或包含无效条目；原文件保持不变。</exception>
    public ProjectCatalog(string stateDirectory)
    {
        file = StateFiles.PathOf(stateDirectory, StateFiles.Projects);
        projects = AtomicJsonStore.Load<List<ProjectEntry>>(file, out fingerprint) ?? [];
        if (projects.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Path)))
            throw new InvalidDataException($"Invalid project settings; original file preserved: {file}");
    }

    /// <summary>
    /// 登记一个已存在的目录；若已登记则返回原有条目。
    /// </summary>
    /// <param name="path">项目目录，可以是相对路径或带末尾分隔符。</param>
    /// <param name="name">显示名称；为空时使用目录名。</param>
    /// <returns>新建或已有的项目条目。</returns>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    public ProjectEntry Add(string path, string? name = null)
    {
        var normalized = FileSystemPath.Normalize(path);
        if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException(normalized);
        var existing = projects.Find(p => FileSystemPath.AreSame(p.Path, normalized));
        if (existing is not null) return existing;
        var entry = new ProjectEntry(Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(normalized).Name : name, normalized);
        projects.Add(entry);
        return entry;
    }

    /// <summary>
    /// 从列表移除项目（不删除目录）。
    /// </summary>
    /// <param name="id">项目标识。</param>
    public void Remove(string id) => projects.RemoveAll(p => p.Id == id);

    /// <summary>
    /// 把内存中的项目列表保存到磁盘。
    /// </summary>
    /// <remarks>
    /// 乐观并发：只有文件仍与上次加载或保存时一致才会写入，成功后更新指纹，可以继续修改并再次保存。
    /// </remarks>
    /// <exception cref="IOException">文件已被其他客户端修改，需要重新加载后再保存。</exception>
    public void Save() => fingerprint = AtomicJsonStore.SaveIfUnchanged(file, projects, fingerprint);
}
