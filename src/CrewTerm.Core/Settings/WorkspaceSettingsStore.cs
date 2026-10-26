using CrewTerm.Core.IO;
using CrewTerm.Core.Layouts;

namespace CrewTerm.Core.Settings;

/// <summary>
/// <see cref="WorkspaceSettings"/> 的加载与保存。
/// </summary>
/// <remarks>
/// 保存采用乐观并发：必须先 <see cref="Load"/>，若期间其他客户端保存过设置，
/// <see cref="Save"/> 会抛出 <see cref="IOException"/> 而不是覆盖对方的修改。
/// </remarks>
/// <param name="stateDirectory">状态目录。</param>
public sealed class WorkspaceSettingsStore(string stateDirectory)
{
    /// <summary>
    /// 工作区设置文件的完整路径（状态目录下的 <see cref="StateFiles.Workspace"/>）。
    /// </summary>
    private readonly string file = StateFiles.PathOf(stateDirectory, StateFiles.Workspace);

    /// <summary>
    /// 最近一次 <see cref="Load"/> 或 <see cref="Save"/> 时文件内容的 SHA-256 指纹；
    /// 尚未加载或加载时文件不存在则为 <see langword="null"/>（此时只有文件仍不存在才能保存成功）。
    /// </summary>
    private string? fingerprint;

    /// <summary>
    /// 加载设置；文件不存在时返回默认值。
    /// </summary>
    /// <returns>已校验的设置对象。</returns>
    /// <exception cref="InvalidDataException">文件损坏、模板字段无效或终端布局不合法。</exception>
    /// <exception cref="ArgumentOutOfRangeException">并发上限超出 1–32。</exception>
    public WorkspaceSettings Load()
    {
        var settings = AtomicJsonStore.Load<WorkspaceSettings>(file, out fingerprint) ?? new();
        Validate(settings);
        return settings;
    }

    /// <summary>
    /// 校验并保存设置。
    /// </summary>
    /// <param name="settings">要保存的设置。</param>
    /// <exception cref="IOException">设置已被其他客户端修改，或文件锁被长时间占用。</exception>
    /// <exception cref="ArgumentOutOfRangeException">并发上限超出 1–32。</exception>
    /// <exception cref="InvalidDataException">模板字段无效或终端布局不合法。</exception>
    public void Save(WorkspaceSettings settings)
    {
        Validate(settings);
        fingerprint = AtomicJsonStore.SaveIfUnchanged(file, settings, fingerprint);
    }

    /// <summary>
    /// 校验设置的取值范围；无论加载还是保存都执行，防止把无效值写入磁盘。
    /// </summary>
    /// <param name="settings">要校验的设置。</param>
    /// <exception cref="ArgumentOutOfRangeException">并发上限超出 1–32。</exception>
    /// <exception cref="InvalidDataException">
    /// 模板列表为 <c>null</c>、某个模板缺少名称/命令/工作目录、布局字典为 <c>null</c>，
    /// 或某个项目布局为 <c>null</c> 或未通过 <see cref="TerminalLayout.Validate"/>。
    /// </exception>
    private static void Validate(WorkspaceSettings settings)
    {
        if (settings.MaximumConcurrentTasks is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(settings), "Concurrency must be between 1 and 32");
        if (settings.Templates is null) throw new InvalidDataException("Templates must be an array");
        if (settings.Templates.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Command)
                || string.IsNullOrWhiteSpace(t.WorkingDirectory)))
            throw new InvalidDataException("Each command template needs a name, a command and a working directory");
        if (settings.ProjectLayouts is null) throw new InvalidDataException("Project layouts must be an object");
        foreach (var layout in settings.ProjectLayouts.Values)
        {
            if (layout is null) throw new InvalidDataException("Project layout cannot be null");
            layout.Validate();
        }
    }
}
