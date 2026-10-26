using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Tests.Tasks;

/// <summary>
/// 覆盖 <see cref="TaskHistory"/> 的任务结果持久化：数量上限、按运行标识更新而不重复、损坏或结构非法文件的保护，以及非法结果的拒绝。
/// 每个测试在系统临时目录下独立的状态目录中读写真实的 <c>task-history.json</c>，测试结束后整体删除。
/// </summary>
public sealed class TaskHistoryTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时状态目录（名称带随机 GUID）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-history-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时状态目录。
    /// </summary>
    public TaskHistoryTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：上限为 2 时连续记录 3 次运行，重新打开后只保留最新的两条（按 <c>three</c>、<c>two</c> 从新到旧排列），
    /// 且退出码、输出和失败状态都按原样恢复。
    /// </summary>
    [Fact]
    public void ReopeningRestoresRealResultsAndKeepsNewestWithinLimit()
    {
        var history = new TaskHistory(root, maxEntries: 2);
        history.Add(Result("one", 1));
        history.Add(Result("two", 2));
        history.Add(Result("three", 3));
        var reopened = new TaskHistory(root, maxEntries: 2).List();
        Assert.Equal(new[] { "three", "two" }, reopened.Select(r => r.Id));
        Assert.Equal(7, reopened[0].ExitCode);
        Assert.Equal("recorded output", reopened[0].Output);
        Assert.False(reopened[0].Succeeded);
    }

    /// <summary>
    /// 验证：两个历史实例交替写入后，再次以已有运行标识 <c>one</c> 写入会更新该条记录而不是新增重复项；
    /// 从另一个实例读取时共 2 条记录，<c>one</c> 的输出为更新后的内容。
    /// </summary>
    [Fact]
    public void UpdatingExistingRunDoesNotDuplicateItsId()
    {
        var first = new TaskHistory(root);
        var second = new TaskHistory(root);
        first.Add(Result("one", 1));
        second.Add(Result("two", 2));
        first.Add(Result("one", 3) with { Output = "updated" });
        var rows = second.List();
        Assert.Equal(2, rows.Count);
        Assert.Equal("updated", rows.Single(r => r.Id == "one").Output);
    }

    /// <summary>
    /// 验证：<c>task-history.json</c> 内容不是有效 JSON 时，构造 <see cref="TaskHistory"/> 抛出 <see cref="InvalidDataException"/>，
    /// 文件原始内容保持不变。
    /// </summary>
    [Fact]
    public void CorruptHistoryIsReportedWithoutReplacingOriginalBytes()
    {
        var path = Path.Combine(root, "task-history.json");
        File.WriteAllText(path, "{broken history");
        Assert.Throws<InvalidDataException>(() => new TaskHistory(root));
        Assert.Equal("{broken history", File.ReadAllText(path));
    }

    /// <summary>
    /// 验证：历史文件是合法 JSON 但结构非法（含 <c>null</c> 元素或缺少字段的空对象）时，构造 <see cref="TaskHistory"/> 抛出
    /// <see cref="InvalidDataException"/>，文件不会被覆盖。
    /// </summary>
    /// <param name="original">写入历史文件的结构非法内容。</param>
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    public void StructurallyInvalidHistoryIsReportedWithoutOverwriting(string original)
    {
        var path = Path.Combine(root, "task-history.json");
        File.WriteAllText(path, original);
        Assert.Throws<InvalidDataException>(() => new TaskHistory(root));
        Assert.Equal(original, File.ReadAllText(path));
    }

    /// <summary>
    /// 验证：添加运行标识为空的非法结果时抛出 <see cref="ArgumentException"/>，已有的 <c>kept</c> 记录保持不变且仍是唯一一条。
    /// </summary>
    [Fact]
    public void InvalidNewResultDoesNotOverwriteHistory()
    {
        var history = new TaskHistory(root);
        history.Add(Result("kept", 1));
        Assert.Throws<ArgumentException>(() => history.Add(Result("", 2)));
        Assert.Equal("kept", Assert.Single(history.List()).Id);
    }

    /// <summary>
    /// 构造一条失败的运行结果：名称 <c>test</c>、命令 <c>exit /b 7</c>、工作目录为 <see cref="root"/>，
    /// 开始于 2026-10-26 01:<paramref name="minute"/>（UTC）并在 1 秒后结束，退出码 7，输出 <c>recorded output</c>，未取消。
    /// </summary>
    /// <param name="id">运行标识。</param>
    /// <param name="minute">开始时间的分钟数，用于区分记录的先后。</param>
    /// <returns>构造的运行结果。</returns>
    private TaskRunResult Result(string id, int minute)
    {
        var start = new DateTimeOffset(2026, 10, 26, 1, minute, 0, TimeSpan.Zero);
        return new(id, "test", "exit /b 7", root, start, start.AddSeconds(1), 7, "recorded output", false);
    }

    /// <summary>
    /// 递归删除本测试实例的临时状态目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
