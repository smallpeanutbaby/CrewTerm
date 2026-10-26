using System.Text.Json;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.Automation.Tests.Plugins;

/// <summary>
/// 端到端验证仓库随附的示例插件 <c>examples/plugins/workspace-notes</c> 确实可用：把它复制到临时目录安装，
/// 再通过 <see cref="LocalPluginRunner"/> 以真实的 PowerShell 进程（Windows 上为系统自带的 Windows PowerShell，其他平台为 <c>pwsh</c>）运行其命令和事件 hook，检查输出与写出的文件。
/// </summary>
public sealed class LocalPluginSampleTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录（以随机 GUID 命名），存放插件副本、示例工作区和状态目录；不预先创建。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermPluginSample", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 验证：从测试输出目录向上查找 <c>CrewTerm.slnx</c> 定位仓库根目录后，示例插件目录确实存在；
    /// 安装到含中文路径的示例插件，其 <c>summary</c> 命令能统计名称含 <c>&amp;</c> 的真实工作区（2 个文件、1 个目录、未截断，
    /// 名称列表包含 <c>说明.md</c>），并原样回报工作目录；随后触发 <c>session.created</c> 事件时，插件的 hook 成功运行，
    /// 在插件目录的 <c>events</c> 子目录写入一条记录了事件名、负载会话标识与 <c>kind</c> 为 <c>event</c> 的 JSON 笔记。
    /// </summary>
    /// <param name="uiPayload"><see langword="true"/> 时以界面使用的 <c>projectRoot</c> 字段传递工作区路径，否则以 <c>workingDirectory</c> 字段传递。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShippedWorkspaceNotesPluginSummarizesRealFilesAndRecordsRealEvents(bool uiPayload)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CrewTerm.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var source = Path.Combine(repository.FullName, "examples", "plugins", "workspace-notes");
        Assert.True(Directory.Exists(source), "The working local plugin example must ship in the repository.");
        var installed = Directory.CreateDirectory(Path.Combine(root, "plugin copy 中文")).FullName;
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace & literal")).FullName;
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "hello.cs"), "class Hello {}");
        File.WriteAllText(Path.Combine(workspace, "说明.md"), "example");
        var catalog = new LocalPluginCatalog(Path.Combine(root, "state"));
        catalog.Install(installed, enabled: true);
        var runner = new LocalPluginRunner(catalog);

        var summary = await runner.RunAsync("crewterm.workspace-notes", "summary",
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { [uiPayload ? "projectRoot" : "workingDirectory"] = workspace }));

        Assert.True(summary.Success, summary.Error + summary.Output);
        using var report = JsonDocument.Parse(summary.Output);
        Assert.Equal(workspace, report.RootElement.GetProperty("workingDirectory").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("files").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("directories").GetInt32());
        Assert.False(report.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("说明.md", report.RootElement.GetProperty("names").EnumerateArray().Select(n => n.GetString()));

        var events = await runner.RunEventAsync("session.created",
            JsonSerializer.SerializeToElement(new { sessionId = "crew-session-1", workingDirectory = workspace }));

        Assert.True(Assert.Single(events).Success);
        var saved = Assert.Single(Directory.EnumerateFiles(Path.Combine(installed, "events"), "*.json"));
        using var note = JsonDocument.Parse(File.ReadAllText(saved));
        Assert.Equal("session.created", note.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("crew-session-1", note.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
        Assert.Equal("event", note.RootElement.GetProperty("kind").GetString());
    }

    /// <summary>
    /// 若临时根目录已被创建，则递归删除它及其中所有文件。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
