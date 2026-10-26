using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Tests.Tasks;

/// <summary>
/// 覆盖 <see cref="BoundedTextBuffer"/> 只保留最近文本的行为：尾部内容的精确性、截断标记、清空重置、
/// 代理对边界处理与容量校验。均为纯内存单元测试。
/// </summary>
public sealed class BoundedTextBufferTests
{
    /// <summary>
    /// 验证：容量为 10 的缓冲区连续追加 1000 段文本，每次追加后保存的内容都恰好等于全部文本的最后 10 个字符
    /// （不足 10 个时为全部文本），最终标记为已截断。
    /// </summary>
    [Fact]
    public void RetainsExactTailAcrossManyAppends()
    {
        var buffer = new BoundedTextBuffer(10);
        var all = new System.Text.StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            var chunk = $"<{i}>";
            buffer.Append(chunk);
            all.Append(chunk);
            Assert.Equal(all.ToString()[^Math.Min(10, all.Length)..], buffer.ToString());
        }
        Assert.True(buffer.Truncated);
    }

    /// <summary>
    /// 验证：恰好填满 8 个字符的容量时内容完整且未标记截断；再多追加 1 个字符即标记为已截断；
    /// <see cref="BoundedTextBuffer.Clear"/> 后内容为空且截断标记被重置。
    /// </summary>
    [Fact]
    public void OutputWithinCapacityIsNotTruncatedAndClearResets()
    {
        var buffer = new BoundedTextBuffer(8);
        buffer.Append("abc");
        buffer.Append("defgh");
        Assert.Equal("abcdefgh", buffer.ToString());
        Assert.False(buffer.Truncated);
        buffer.Append("i");
        Assert.True(buffer.Truncated);
        buffer.Clear();
        Assert.Equal("", buffer.ToString());
        Assert.False(buffer.Truncated);
    }

    /// <summary>
    /// 验证：容量为 2 时，若保留最后 2 个 UTF-16 代码单元会从 Emoji 代理对的中间截断，则丢弃孤立的低代理项，只保留 <c>c</c>。
    /// </summary>
    [Fact]
    public void TailNeverStartsWithHalfOfASurrogatePair()
    {
        var buffer = new BoundedTextBuffer(2);
        buffer.Append("ab\U0001F600c");
        Assert.Equal("c", buffer.ToString());
    }

    /// <summary>
    /// 验证：容量为 0 或 <see cref="int.MaxValue"/> 时，构造函数抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    [Fact]
    public void RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedTextBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedTextBuffer(int.MaxValue));
    }
}
