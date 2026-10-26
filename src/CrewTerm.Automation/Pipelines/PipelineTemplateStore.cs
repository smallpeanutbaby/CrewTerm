using CrewTerm.Core.IO;

namespace CrewTerm.Automation.Pipelines;

/// <summary>
/// 流水线模板的持久化存储（<see cref="StateFiles.PipelineTemplates"/>）。
/// </summary>
/// <remarks>
/// 模板标识不区分大小写唯一。修改在跨进程互斥锁内完成，多个 UI 客户端同时保存不会丢失彼此的模板。
/// </remarks>
public sealed class PipelineTemplateStore
{
    /// <summary>
    /// 模板文件 <c>pipeline-templates.json</c> 的绝对路径。
    /// </summary>
    private readonly string path;

    /// <summary>
    /// 打开状态目录中的模板存储，并立即校验现有文件。
    /// </summary>
    /// <param name="stateDirectory">状态目录，不存在时自动创建。</param>
    /// <exception cref="InvalidDataException">现有模板文件损坏。</exception>
    public PipelineTemplateStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        StateFiles.EnsureDirectory(stateDirectory);
        path = StateFiles.PathOf(stateDirectory, StateFiles.PipelineTemplates);
        _ = List();
    }

    /// <summary>
    /// 列出全部模板（保存顺序）。
    /// </summary>
    /// <returns>模板列表。</returns>
    public IReadOnlyList<PipelineTemplate> List()
        => Validated(AtomicJsonStore.Read(path, () => new List<PipelineTemplate>())).ToArray();

    /// <summary>
    /// 新增模板，或替换标识相同的模板。
    /// </summary>
    /// <param name="template">要保存的模板。</param>
    /// <exception cref="ArgumentException">模板无效（无名称、无步骤或步骤命令为空）。</exception>
    public void Upsert(PipelineTemplate template)
    {
        Validate(template);
        AtomicJsonStore.Update(path, () => new List<PipelineTemplate>(), rows =>
        {
            Validated(rows).RemoveAll(t => t.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
            rows.Add(template);
            return true;
        });
    }

    /// <summary>
    /// 删除模板。
    /// </summary>
    /// <param name="id">模板标识。</param>
    /// <returns>找到并删除时返回 <see langword="true"/>。</returns>
    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = false;
        AtomicJsonStore.Update(path, () => new List<PipelineTemplate>(), rows =>
            removed = Validated(rows).RemoveAll(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    /// <summary>
    /// 校验从磁盘读到的全部模板，把字段错误转换为“文件损坏”异常。
    /// </summary>
    /// <param name="templates">从文件读取的模板。</param>
    /// <returns>原列表（便于链式调用）。</returns>
    /// <exception cref="InvalidDataException">存在无效模板。</exception>
    private List<PipelineTemplate> Validated(List<PipelineTemplate> templates)
    {
        try { foreach (var template in templates) Validate(template); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid pipeline templates; original file preserved: {path}", error);
        }
        return templates;
    }

    /// <summary>
    /// 校验单个模板：标识、名称必填，至少一个步骤，每个步骤的名称和命令必填。
    /// </summary>
    /// <param name="template">要校验的模板。</param>
    /// <exception cref="ArgumentException">模板无效。</exception>
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
