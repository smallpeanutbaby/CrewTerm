using CrewTerm.Core.IO;

namespace CrewTerm.Automation.Tasks;

/// <summary>
/// 已完成后台任务的持久化历史（<see cref="StateFiles.TaskHistory"/>），按开始时间倒序保留最近若干条。
/// </summary>
/// <remarks>
/// 写入在跨进程互斥锁内完成“读取 → 合并 → 写回”，多个实例或进程同时追加不会互相覆盖。
/// 文件损坏或包含无效记录时抛出 <see cref="InvalidDataException"/>，原文件保持不变。
/// </remarks>
public sealed class TaskHistory
{
    /// <summary>
    /// 历史文件 <c>task-history.json</c> 的绝对路径。
    /// </summary>
    private readonly string path;

    /// <summary>
    /// 最多保留的记录数（正整数）；读取和写入时都会按开始时间倒序裁剪到这个数量。
    /// </summary>
    private readonly int maxEntries;

    /// <summary>
    /// 打开状态目录中的任务历史，并立即校验现有文件。
    /// </summary>
    /// <param name="stateDirectory">状态目录，不存在时自动创建。</param>
    /// <param name="maxEntries">最多保留的记录数。</param>
    /// <exception cref="InvalidDataException">现有历史文件损坏。</exception>
    public TaskHistory(string stateDirectory, int maxEntries = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        Directory.CreateDirectory(stateDirectory);
        path = StateFiles.PathOf(stateDirectory, StateFiles.TaskHistory);
        this.maxEntries = maxEntries;
        _ = List();
    }

    /// <summary>
    /// 列出历史记录，最新开始的在前。
    /// </summary>
    /// <returns>最多 <c>maxEntries</c> 条记录。</returns>
    public IReadOnlyList<TaskRunResult> List()
        => Validated(AtomicJsonStore.Read(path, () => new List<TaskRunResult>()))
            .OrderByDescending(r => r.StartedAt).Take(maxEntries).ToArray();

    /// <summary>
    /// 追加或替换（按 <see cref="TaskRunResult.Id"/>）一条记录，并裁剪到上限。
    /// </summary>
    /// <param name="result">已完成任务的结果。</param>
    /// <exception cref="ArgumentException">记录字段无效；此时历史文件不会被修改。</exception>
    public void Add(TaskRunResult result)
    {
        Validate(result);
        AtomicJsonStore.Update(path, () => new List<TaskRunResult>(), rows =>
        {
            var merged = Validated(rows).Where(r => r.Id != result.Id).Append(result)
                .OrderByDescending(r => r.StartedAt).Take(maxEntries).ToList();
            rows.Clear();
            rows.AddRange(merged);
            return true;
        });
    }

    /// <summary>
    /// 校验从磁盘读到的全部记录，把字段错误转换为“文件损坏”异常。
    /// </summary>
    /// <param name="rows">从文件读取的记录。</param>
    /// <returns>原列表（便于链式调用）。</returns>
    /// <exception cref="InvalidDataException">存在无效记录。</exception>
    private List<TaskRunResult> Validated(List<TaskRunResult> rows)
    {
        try { foreach (var row in rows) Validate(row); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid task history; original file preserved: {path}", error);
        }
        return rows;
    }

    /// <summary>
    /// 校验单条记录的必填字段与时间顺序。
    /// </summary>
    /// <param name="result">要校验的记录。</param>
    /// <exception cref="ArgumentException">字段为空或结束时间早于开始时间。</exception>
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
