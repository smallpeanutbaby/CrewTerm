using System.Text;

namespace CrewTerm.Automation.Tasks;

/// <summary>
/// 只保留最近 <see cref="Capacity"/> 个字符的文本缓冲，用于收集长时间运行命令的输出。
/// </summary>
/// <remarks>
/// 每次追加都从头部删除多余字符的做法，在输出量远大于容量时会退化为平方级复制。
/// 这里允许内部缓冲增长到容量的两倍后再一次性裁剪，使追加的均摊成本与块大小成正比。
/// 本类不是线程安全的，调用方负责加锁。
/// </remarks>
public sealed class BoundedTextBuffer
{
    /// <summary>
    /// 内部缓冲；长度可能暂时超过 <see cref="Capacity"/>，达到两倍容量时才一次性裁剪到 <see cref="Capacity"/>。
    /// </summary>
    private readonly StringBuilder text = new();

    /// <summary>
    /// 创建缓冲。
    /// </summary>
    /// <param name="capacity">保留的最大字符数。</param>
    public BoundedTextBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, int.MaxValue / 2);
        Capacity = capacity;
    }

    /// <summary>
    /// 保留的最大字符数。
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// 是否曾因超出容量而丢弃过开头的内容。
    /// </summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// 追加文本。
    /// </summary>
    /// <param name="value">要追加的文本。</param>
    public void Append(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        text.Append(value);
        if (text.Length > Capacity) Truncated = true;
        if (text.Length >= Capacity * 2) text.Remove(0, text.Length - Capacity);
    }

    /// <summary>
    /// 清空内容并重置截断标记。
    /// </summary>
    public void Clear()
    {
        text.Clear();
        Truncated = false;
    }

    /// <summary>
    /// 返回最近的至多 <see cref="Capacity"/> 个字符。
    /// </summary>
    /// <returns>保留的文本；不会以被截断的半个代理对开头。</returns>
    public override string ToString()
    {
        // Append 中的批量裁剪和这里的截取都可能切开代理对，因此统一跳过开头孤立的低代理项。
        var start = Math.Max(0, text.Length - Capacity);
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        return text.ToString(start, text.Length - start);
    }
}
