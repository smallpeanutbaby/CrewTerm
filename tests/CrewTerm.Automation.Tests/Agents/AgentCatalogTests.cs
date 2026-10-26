using System.Runtime.Versioning;
using CrewTerm.Automation.Agents;

namespace CrewTerm.Automation.Tests.Agents;

/// <summary>
/// 覆盖 <see cref="AgentCatalog"/> 中 Agent 配置的持久化、内置配置保护、校验、损坏文件保护和
/// <see cref="AgentProfile.CommandLine"/> 的引号处理（程序查找规则见 Core 测试中的 ExecutableLocatorTests）。
/// 每个测试在系统临时目录下独立的子目录中读写真实文件，测试结束后整体删除。
/// </summary>
public sealed class AgentCatalogTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录，既用作 <see cref="AgentCatalog"/> 的状态目录，也存放测试用的命令脚本。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-agents-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public AgentCatalogTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：以相同标识两次写入自定义配置时后一次覆盖前一次，重新加载得到唯一一条参数为 <c>--mode revised</c> 的非内置配置；
    /// 删除该配置返回 <see langword="true"/>，再次加载后它不再存在。
    /// </summary>
    [Fact]
    public void CustomProfilePersistsUpdatesAndRemoval()
    {
        var catalog = new AgentCatalog(root);
        catalog.Upsert(new("custom", "Custom agent", "custom-cli", "--mode first"));
        catalog.Upsert(new("custom", "Custom agent", "custom-cli", "--mode revised"));
        var loaded = new AgentCatalog(root);
        var profile = Assert.Single(loaded.Profiles, p => p.Id == "custom");
        Assert.Equal("--mode revised", profile.Arguments);
        Assert.False(profile.IsBuiltIn);
        Assert.True(loaded.Remove("custom"));
        Assert.DoesNotContain(new AgentCatalog(root).Profiles, p => p.Id == "custom");
    }

    /// <summary>
    /// 验证：内置的 <c>shell</c> 配置不能被删除（<see cref="AgentCatalog.Remove"/> 返回 <see langword="false"/>），
    /// 并且 <see cref="AgentCatalog.Discover"/> 仍把它报告为可用。
    /// </summary>
    [Fact]
    public void BuiltinProfilesAreNotDeletedFromCatalog()
    {
        var catalog = new AgentCatalog(root);
        Assert.False(catalog.Remove("shell"));
        Assert.Contains(catalog.Discover(), a => a.Profile.Id == "shell" && a.IsAvailable);
    }

    /// <summary>
    /// 验证：标识含空格、可执行文件为空白的非法配置被拒绝（抛出 <see cref="ArgumentException"/>），
    /// 已保存的 <c>kept</c> 配置在重新加载后依然存在。
    /// </summary>
    [Fact]
    public void InvalidProfileDoesNotReplaceExistingData()
    {
        var catalog = new AgentCatalog(root);
        catalog.Upsert(new("kept", "Kept", "my-command"));
        Assert.Throws<ArgumentException>(() => catalog.Upsert(new("bad id", "Bad", " ")));
        Assert.Contains(new AgentCatalog(root).Profiles, p => p.Id == "kept");
    }

    /// <summary>
    /// 验证：<c>agent-profiles.json</c> 内容不是有效 JSON 时，构造 <see cref="AgentCatalog"/> 抛出 <see cref="InvalidDataException"/>，
    /// 文件原始内容保持不变。
    /// </summary>
    [Fact]
    public void CorruptProfilesAreReportedAndPreserved()
    {
        var path = Path.Combine(root, "agent-profiles.json");
        File.WriteAllText(path, "[not json");
        Assert.Throws<InvalidDataException>(() => new AgentCatalog(root));
        Assert.Equal("[not json", File.ReadAllText(path));
    }

    /// <summary>
    /// 验证：可执行文件路径含空格时，<see cref="AgentProfile.CommandLine"/> 用双引号包住路径，再接上原样的参数。
    /// </summary>
    [Fact]
    public void ExecutableWithSpacesIsQuotedForLaunch()
    {
        var profile = new AgentProfile("custom", "Custom", "C:\\my tools\\agent.exe", "--interactive");
        Assert.Equal("\"C:\\my tools\\agent.exe\" --interactive", profile.CommandLine);
    }

    /// <summary>
    /// 验证（Linux / macOS）：<c>crewterm doctor</c> 等只读取 Agent 配置的入口第一次遇到不存在的状态目录时，
    /// <see cref="AgentCatalog"/> 创建的目录只允许当前用户访问（<c>0700</c>），而不是按 umask 创建出其他用户可读的目录。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void CatalogCreatesPrivateStateDirectoryOnUnix()
    {
        var state = Path.Combine(root, "new state");
        _ = new AgentCatalog(state);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(state));
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
