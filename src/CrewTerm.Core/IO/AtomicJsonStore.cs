using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrewTerm.Core.IO;

/// <summary>
/// 状态目录中 JSON 文件的统一读写入口：原子替换、损坏保护与跨进程互斥。
/// </summary>
/// <remarks>
/// <para>多个 UI 客户端和 daemon 可能同时访问同一个状态目录，因此提供两种并发策略：</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="SaveIfUnchanged{T}"/>：乐观并发。调用方持有读取时的指纹，保存时若文件已被
/// 其他客户端修改则拒绝覆盖，由用户决定重新加载（项目列表、工作区设置）。
/// </description></item>
/// <item><description>
/// <see cref="Update{T}"/>：悲观并发。在命名互斥锁内完成“读取 → 修改 → 写回”，
/// 适合追加式数据（任务历史、Agent 配置、流水线模板）。
/// </description></item>
/// </list>
/// <para>两种策略使用同一把按文件路径派生的命名互斥锁，可以混用。
/// 读到损坏的 JSON 时抛出 <see cref="InvalidDataException"/> 并保留原始字节，绝不静默覆盖。</para>
/// </remarks>
public static class AtomicJsonStore
{
    /// <summary>
    /// 所有状态文件共用的 JSON 选项：带缩进，便于用户直接查看和手工修复；属性名保持 C# 原样（区分大小写）。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// 等待跨进程文件锁的最长时间（5 秒）；超时抛出 <see cref="IOException"/>，而不是无限阻塞。
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 读取 JSON 文件；文件不存在时返回 <see langword="default"/>。
    /// </summary>
    /// <typeparam name="T">反序列化目标类型。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <returns>反序列化结果，或文件不存在时的默认值。</returns>
    /// <exception cref="InvalidDataException">文件内容不是有效 JSON 或为 <c>null</c>。</exception>
    public static T? Load<T>(string path)
        => Load<T>(path, out _);

    /// <summary>
    /// 读取 JSON 文件，并返回读取时文件内容的指纹，供 <see cref="SaveIfUnchanged{T}"/> 使用。
    /// </summary>
    /// <typeparam name="T">反序列化目标类型。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="fingerprint">文件字节的 SHA-256；文件不存在时为 <see langword="null"/>。</param>
    /// <returns>反序列化结果，或文件不存在时的默认值。</returns>
    /// <exception cref="InvalidDataException">文件内容不是有效 JSON 或为 <c>null</c>。</exception>
    public static T? Load<T>(string path, out string? fingerprint)
    {
        fingerprint = null;
        if (!File.Exists(path)) return default;
        try
        {
            var bytes = AtomicFile.ReadAllBytes(path);
            fingerprint = Hash(bytes);
            return JsonSerializer.Deserialize<T>(bytes, Options)
                ?? throw new InvalidDataException($"State file contains null: {path}");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Cannot read state file; original file preserved: {path}", error);
        }
    }

    /// <summary>
    /// 读取 JSON 文件；文件不存在时返回 <paramref name="createEmpty"/> 创建的空值。
    /// </summary>
    /// <typeparam name="T">反序列化目标类型。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="createEmpty">文件不存在时的空值工厂。</param>
    /// <returns>文件内容或空值。</returns>
    public static T Read<T>(string path, Func<T> createEmpty) where T : class
        => Load<T>(path) ?? createEmpty();

    /// <summary>
    /// 在跨进程互斥锁内执行“读取 → 修改 → 写回”。
    /// </summary>
    /// <typeparam name="T">可就地修改的状态类型（通常为 <see cref="List{T}"/>）。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="createEmpty">文件不存在时的空值工厂。</param>
    /// <param name="mutate">就地修改状态；返回 <see langword="false"/> 表示无需写回。</param>
    /// <returns>是否写回了文件。</returns>
    /// <exception cref="IOException">其他进程长时间持有锁。</exception>
    public static bool Update<T>(string path, Func<T> createEmpty, Func<T, bool> mutate) where T : class
        => WithFileLock(path, () =>
        {
            var value = Read(path, createEmpty);
            if (!mutate(value)) return false;
            Save(path, value);
            return true;
        });

    /// <summary>
    /// 仅当文件仍与读取时一致（指纹相同）时才保存，否则拒绝覆盖。
    /// </summary>
    /// <typeparam name="T">序列化类型。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="value">要保存的值。</param>
    /// <param name="fingerprint">读取时得到的指纹；文件原本不存在时为 <see langword="null"/>。</param>
    /// <returns>保存后新内容的指纹，供下一次保存使用。</returns>
    /// <exception cref="IOException">文件已被其他客户端修改，或锁被长时间占用。</exception>
    public static string SaveIfUnchanged<T>(string path, T value, string? fingerprint)
        => WithFileLock(path, () =>
        {
            var actual = File.Exists(path) ? Hash(AtomicFile.ReadAllBytes(path)) : null;
            if (actual != fingerprint) throw new IOException($"Settings changed in another client. Reload before saving: {path}");
            var bytes = Serialize(value);
            WriteBytes(path, bytes);
            return Hash(bytes);
        });

    /// <summary>
    /// 无条件原子保存（不做并发检查），目录不存在时自动创建。
    /// </summary>
    /// <typeparam name="T">序列化类型。</typeparam>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="value">要保存的值。</param>
    public static void Save<T>(string path, T value) => WriteBytes(path, Serialize(value));

    /// <summary>
    /// 按统一的格式（缩进、UTF-8 无 BOM）序列化状态值。
    /// </summary>
    /// <typeparam name="T">序列化类型。</typeparam>
    /// <param name="value">要序列化的值。</param>
    /// <returns>UTF-8 编码的 JSON 字节。</returns>
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// 确保目录存在后原子写入已序列化的内容。
    /// </summary>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="bytes">要写入的完整内容。</param>
    private static void WriteBytes(string path, byte[] bytes)
    {
        // 状态文件都直接位于状态目录中：目录尚不存在时按状态目录的规则创建（Unix 上为 0700），
        // 而不是按 umask 创建出其他用户可读的目录。
        StateFiles.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// 在以文件路径命名的系统互斥锁内执行操作。锁名由规范化路径的哈希派生，
    /// Windows 下先转为大写，使大小写不同的同一路径共用一把锁。
    /// </summary>
    /// <typeparam name="TResult">操作的返回类型。</typeparam>
    /// <param name="path">被保护的 JSON 文件路径（可以是相对路径）。</param>
    /// <param name="action">持锁期间执行的操作；它抛出的异常在释放锁后原样传出。</param>
    /// <returns><paramref name="action"/> 的返回值。</returns>
    /// <exception cref="IOException">在 <see cref="LockTimeout"/> 内没有拿到锁。</exception>
    private static TResult WithFileLock<TResult>(string path, Func<TResult> action)
    {
        var key = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        using var gate = new Mutex(false, "CrewTerm-json-" + Hash(Encoding.UTF8.GetBytes(key)));
        var held = false;
        try
        {
            // 持锁进程崩溃会留下“被遗弃”的互斥锁；此时文件本身仍由原子替换保护，可以安全接管。
            try { held = gate.WaitOne(LockTimeout); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Settings are busy in another client");
            return action();
        }
        finally { if (held) gate.ReleaseMutex(); }
    }

    /// <summary>
    /// 计算字节内容的 SHA-256，返回大写十六进制字符串，用作文件指纹与互斥锁名。
    /// </summary>
    /// <param name="bytes">文件内容。</param>
    /// <returns>64 位十六进制哈希。</returns>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
