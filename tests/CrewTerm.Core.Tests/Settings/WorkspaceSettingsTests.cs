using CrewTerm.Core.Editing;
using CrewTerm.Core.Settings;

namespace CrewTerm.Core.Tests.Settings;

/// <summary>
/// 覆盖 <see cref="WorkspaceSettingsStore"/> 的持久化、取值校验与多客户端乐观并发行为，以及 <see cref="CodeContext.Build"/> 的代码上下文生成。
/// 设置相关测试在系统临时目录下独立的状态目录中读写真实文件，并在结束时删除；上下文测试为纯内存单元测试。
/// </summary>
public sealed class WorkspaceSettingsTests
{
    /// <summary>
    /// 验证：开启分屏、把并发上限设为 2 并添加一个命令模板后保存，重新加载得到的设置保留上述三项值。
    /// </summary>
    [Fact]
    public void TemplatesAndLayoutSurviveRestart()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceSettingsStore(state);
            var settings = store.Load();
            settings.SplitTerminals = true;
            settings.MaximumConcurrentTasks = 2;
            settings.Templates.Add(new("verify", "dotnet test", "D:\\代码\\project"));
            store.Save(settings);
            var restored = store.Load();
            Assert.True(restored.SplitTerminals);
            Assert.Equal(2, restored.MaximumConcurrentTasks);
            Assert.Equal("dotnet test", Assert.Single(restored.Templates).Command);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    /// <summary>
    /// 验证：并发任务上限超出 1～32 范围时，<see cref="WorkspaceSettingsStore.Save"/> 抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <param name="count">超出允许范围的并发上限（0 或 33）。</param>
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void InvalidConcurrencyCannotBeSaved(int count)
    {
        // 使用独立的临时目录：即使校验回归，也不会把 workspace.json 写进系统临时目录。
        var state = Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceSettingsStore(state);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new() { MaximumConcurrentTasks = count }));
            Assert.False(File.Exists(Path.Combine(state, "workspace.json")));
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    /// <summary>
    /// 验证：对以 CRLF 分隔的三行文本只选取第 2 行时，生成的上下文包含相对位置 <c>file.cs:2-2</c> 和第 2 行内容，
    /// 而不包含第 1 行与第 3 行。
    /// </summary>
    [Fact]
    public void ContextIncludesOnlyRequestedLinesAndCorrectRelativeLocation()
    {
        var root = Path.GetFullPath(".");
        var result = CodeContext.Build(root, Path.Combine(root, "file.cs"), "first\r\nsecond\r\nthird", 2, 2, "Review this");
        Assert.Contains("file.cs:2-2", result);
        Assert.Contains("second", result);
        Assert.DoesNotContain("first", result);
        Assert.DoesNotContain("third", result);
    }

    /// <summary>
    /// 验证：文本只有 1 行却请求第 2～4 行时，<see cref="CodeContext.Build"/> 抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    [Fact]
    public void ContextRejectsInvalidLineRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeContext.Build(Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "file.cs"), "one", 2, 4, "review"));
    }

    /// <summary>
    /// 验证：两个存储实例基于同一份设置各自添加模板，第一个先保存后，第二个基于过期内容保存时抛出 <see cref="IOException"/>，
    /// 磁盘上只保留第一个实例添加的模板 <c>A</c>，不会被静默覆盖。
    /// </summary>
    [Fact]
    public void ConcurrentTemplateEditsAreReportedInsteadOfOverwritten()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new WorkspaceSettingsStore(state);
            var second = new WorkspaceSettingsStore(state);
            var a = first.Load();
            var b = second.Load();
            a.Templates.Add(new("A", "echo A", state));
            first.Save(a);
            b.Templates.Add(new("B", "echo B", state));
            Assert.Throws<IOException>(() => second.Save(b));
            Assert.Equal("A", Assert.Single(first.Load().Templates).Name);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    /// <summary>
    /// 验证：<c>workspace.json</c> 中的命令模板为 <c>null</c> 或缺少命令时，加载以 <see cref="InvalidDataException"/> 失败，
    /// 原文件保持不变；不会让无效模板进入界面后再崩溃。
    /// </summary>
    /// <param name="templates">写入文件的 <c>Templates</c> 数组 JSON。</param>
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Name\":\"build\",\"Command\":\"\",\"WorkingDirectory\":\"D:\\\\work\"}]")]
    public void InvalidCommandTemplatesAreRejectedOnLoad(string templates)
    {
        var state = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CrewTermSettings", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var file = Path.Combine(state, "workspace.json");
            var json = "{\"MaximumConcurrentTasks\":4,\"Templates\":" + templates + "}";
            File.WriteAllText(file, json);
            Assert.Throws<InvalidDataException>(() => new WorkspaceSettingsStore(state).Load());
            Assert.Equal(json, File.ReadAllText(file));
        }
        finally { Directory.Delete(state, true); }
    }
}
