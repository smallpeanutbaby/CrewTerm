using CrewTerm.Core.IO;

namespace CrewTerm.Automation.Providers;

/// <summary>
/// 终端会话的持久化目录（<c>native-sessions.json</c>），采用“读取 → 修改 → 写回”方式更新，
/// 保留已无进程的休眠会话，直到用户显式忘记。
/// </summary>
/// <remarks>
/// 每次修改都在跨进程互斥锁内重新读取并校验整个文件，只改动目标记录，
/// 因此不会覆盖其他休眠记录或其他进程同时新增的记录。
/// 文件损坏或含有无效、重复记录时抛出 <see cref="InvalidDataException"/>，原文件保持不变。
/// </remarks>
public sealed class NativeSessionStore
{
    /// <summary>
    /// 目录文件 <c>native-sessions.json</c> 的绝对路径。
    /// </summary>
    private readonly string path;

    /// <summary>
    /// 打开 CrewTerm 状态目录中的会话目录，并立即校验现有文件。
    /// </summary>
    /// <param name="stateDirectory">本地状态目录。</param>
    /// <exception cref="InvalidDataException">现有文件损坏或含有无效记录。</exception>
    public NativeSessionStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        path = StateFiles.PathOf(stateDirectory, StateFiles.NativeSessions);
        _ = List();
    }

    /// <summary>
    /// 返回全部记录，包括没有存活进程的会话；按 <see cref="NativeSessionRecord.UpdatedAt"/> 倒序（最近修改的在前）。
    /// </summary>
    /// <returns>记录数组。</returns>
    /// <exception cref="InvalidDataException">文件损坏或含有无效记录。</exception>
    public IReadOnlyList<NativeSessionRecord> List()
    {
        var records = AtomicJsonStore.Read(path, () => new List<NativeSessionRecord>());
        ValidateAll(records);
        return records.OrderByDescending(r => r.UpdatedAt).ToArray();
    }

    /// <summary>
    /// 按稳定的 CrewTerm 会话标识查找记录。
    /// </summary>
    /// <param name="id">CrewTerm 会话标识（序数比较，区分大小写）。</param>
    /// <returns>找到的记录；不存在时为 <see langword="null"/>。</returns>
    /// <exception cref="InvalidDataException">文件损坏或含有无效记录。</exception>
    public NativeSessionRecord? Get(string id) => List().FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// 新增或替换（按标识）一条记录，不会替换其他休眠记录或其他进程同时新增的记录。
    /// </summary>
    /// <remarks>
    /// 保存前把工作目录规范化为不带末尾分隔符的绝对路径，并把 <see cref="NativeSessionRecord.UpdatedAt"/> 设为当前 UTC 时间
    /// （调用方传入的值会被忽略）。
    /// </remarks>
    /// <param name="record">要持久化的记录。</param>
    /// <exception cref="ArgumentException">记录必填字段为空、工作目录不是合法路径，或命令含 NUL；此时文件不会被修改。</exception>
    /// <exception cref="InvalidDataException">现有文件损坏或含有无效记录。</exception>
    public void Upsert(NativeSessionRecord record)
    {
        Validate(record);
        var normalized = record with { WorkingDirectory = FileSystemPath.Normalize(record.WorkingDirectory), UpdatedAt = DateTimeOffset.UtcNow };
        AtomicJsonStore.Update(path, () => new List<NativeSessionRecord>(), records =>
        {
            ValidateAll(records);
            records.RemoveAll(r => r.Id == record.Id);
            records.Add(normalized);
            return true;
        });
    }

    /// <summary>
    /// 为已有记录绑定精确的 Provider 原生对话身份，或用户显式提供的自定义恢复命令。
    /// </summary>
    /// <remarks>
    /// 会同时覆盖记录中的 Provider（转为小写）、原生标识和恢复命令三个字段：传入 <see langword="null"/> 的字段会被清空，
    /// 而不是保留旧值。原生标识按 <see cref="ProviderSessionService.ValidateNativeId"/> 的规则校验，
    /// 只接受精确标识，绝不接受名称、前缀或“最近会话”选择器。
    /// </remarks>
    /// <param name="id">已存在的 CrewTerm 会话标识。</param>
    /// <param name="provider">Provider 标识，非空白。</param>
    /// <param name="nativeSessionId">精确的原生对话标识；未知时为 <see langword="null"/>。</param>
    /// <param name="resumeCommand">显式的自定义恢复命令；没有时为 <see langword="null"/>。</param>
    /// <returns>绑定后的记录。</returns>
    /// <exception cref="ArgumentException">Provider 为空白；原生标识与恢复命令都为空；原生标识格式不符合该 Provider 的要求；或恢复命令含 NUL。</exception>
    /// <exception cref="KeyNotFoundException">记录不存在。</exception>
    /// <exception cref="InvalidDataException">现有文件损坏或含有无效记录。</exception>
    public NativeSessionRecord Bind(string id, string provider, string? nativeSessionId, string? resumeCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (string.IsNullOrWhiteSpace(nativeSessionId) && string.IsNullOrWhiteSpace(resumeCommand))
            throw new ArgumentException("Supply an exact native conversation ID or an explicit custom resume command.");
        NativeSessionRecord? bound = null;
        AtomicJsonStore.Update(path, () => new List<NativeSessionRecord>(), records =>
        {
            ValidateAll(records);
            var index = records.FindIndex(r => r.Id == id);
            if (index < 0) throw new KeyNotFoundException("Saved session not found: " + id);
            bound = records[index] with { Provider = provider.ToLowerInvariant(), NativeSessionId = nativeSessionId,
                ResumeCommand = resumeCommand, UpdatedAt = DateTimeOffset.UtcNow };
            Validate(bound);
            if (nativeSessionId is not null) ProviderSessionService.ValidateNativeId(bound.Provider, nativeSessionId);
            records[index] = bound;
            return true;
        });
        return bound!;
    }

    /// <summary>
    /// 只删除元数据记录；绝不会停止进程，也不会删除 Provider 一侧的对话历史。
    /// </summary>
    /// <param name="id">CrewTerm 会话标识。</param>
    /// <returns>找到并删除时返回 <see langword="true"/>；不存在时返回 <see langword="false"/> 且不修改文件。</returns>
    /// <exception cref="InvalidDataException">现有文件损坏或含有无效记录。</exception>
    public bool Forget(string id) => AtomicJsonStore.Update(path, () => new List<NativeSessionRecord>(), records =>
    {
        ValidateAll(records);
        return records.RemoveAll(r => r.Id == id) > 0;
    });

    /// <summary>
    /// 校验从文件读到的全部记录，并检查标识（序数比较）不重复；把字段错误转换为“文件损坏”异常。
    /// </summary>
    /// <param name="records">从文件读取的记录。</param>
    /// <exception cref="InvalidDataException">存在无效或重复的记录。</exception>
    private static void ValidateAll(List<NativeSessionRecord> records)
    {
        try
        {
            foreach (var record in records) Validate(record);
            if (records.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != records.Count)
                throw new ArgumentException("Duplicate saved session IDs.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid native session catalog; original file preserved.", error); }
    }

    /// <summary>
    /// 校验单条记录：标识、名称、命令、工作目录必填，工作目录必须是可解析的路径，启动命令与恢复命令都不能含 NUL。
    /// </summary>
    /// <param name="record">要校验的记录。</param>
    /// <exception cref="ArgumentException">记录为 <see langword="null"/>、必填字段为空白、命令含 NUL，或工作目录无法被 <see cref="Path.GetFullPath(string)"/> 解析。</exception>
    private static void Validate(NativeSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.WorkingDirectory);
        _ = Path.GetFullPath(record.WorkingDirectory);
        if (record.Command.Contains('\0') || record.ResumeCommand?.Contains('\0') == true)
            throw new ArgumentException("Commands cannot contain NUL characters.");
    }
}
