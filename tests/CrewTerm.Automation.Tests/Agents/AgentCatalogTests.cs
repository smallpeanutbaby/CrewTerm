using System.Runtime.Versioning;
using CrewTerm.Automation.Agents;

namespace CrewTerm.Automation.Tests.Agents;

/// <summary>
/// 覆盖 <see cref="ExecutableLocator"/> 的可执行文件查找规则，以及 <see cref="AgentCatalog"/> 中 Agent 配置的持久化、
/// 内置配置保护、校验、损坏文件保护和 <see cref="AgentProfile.CommandLine"/> 的引号处理。
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
    /// 验证：在显式指定的、含空格的搜索目录中，按 <c>PATHEXT</c> 扩展名列表能找到不带扩展名指定的 <c>example.cmd</c>
    /// （Windows 上路径比较不区分大小写）；不存在的程序返回 <see langword="null"/>。PATHEXT 只在 Windows 上生效。
    /// </summary>
    [WindowsFact]
    public void LocatorFindsWindowsCommandShimsInPathsWithSpaces()
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "my tools")).FullName;
        var command = Path.Combine(bin, "example.cmd");
        File.WriteAllText(command, "@echo off\r\n");
        var found = ExecutableLocator.Find("example", bin, ".EXE;.CMD");
        Assert.Equal(command, found, ignoreCase: OperatingSystem.IsWindows());
        Assert.Null(ExecutableLocator.Find("missing-agent", bin, ".EXE;.CMD"));
    }

    /// <summary>
    /// 验证：目录中同时存在 npm 生成的无扩展名 Unix 脚本和同名 <c>.cmd</c> 脚本时，无论按名称搜索还是直接给出无扩展名脚本的绝对路径，
    /// Windows 上都解析到 <c>.cmd</c> 脚本，其他平台上解析到无扩展名脚本。
    /// </summary>
    /// <param name="absolutePath"><see langword="true"/> 时传入无扩展名脚本的绝对路径，否则只传程序名 <c>example</c>。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocatorPrefersNativeShimWhenExtensionlessScriptAlsoExists(bool absolutePath)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "npm tools")).FullName;
        var unixShim = Path.Combine(bin, "example");
        var windowsShim = unixShim + ".cmd";
        File.WriteAllText(unixShim, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(unixShim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(windowsShim, "@echo off\r\n");

        var found = ExecutableLocator.Find(absolutePath ? unixShim : "example", bin, ".EXE;.CMD");

        Assert.Equal(OperatingSystem.IsWindows() ? windowsShim : unixShim, found,
            ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>
    /// 验证（Linux / macOS）：没有执行权限的同名文件不会被当作程序，加上执行权限后才能找到，与 Shell 的查找规则一致。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void LocatorRequiresExecutePermissionOnUnix()
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "unix tools")).FullName;
        var program = Path.Combine(bin, "example");
        File.WriteAllText(program, "#!/bin/sh\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(ExecutableLocator.Find("example", bin));
        Assert.Null(ExecutableLocator.Find(program));
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(program, ExecutableLocator.Find("example", bin));
    }

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
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
