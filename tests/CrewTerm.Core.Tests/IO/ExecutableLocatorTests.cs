using System.Runtime.Versioning;
using CrewTerm.Core.IO;

namespace CrewTerm.Core.Tests.IO;

/// <summary>
/// 覆盖 <see cref="ExecutableLocator"/> 的可执行文件查找规则：PATHEXT 扩展名、npm 启动脚本、Unix 执行权限，
/// 以及从不在当前目录中查找程序。每个测试在系统临时目录下独立的子目录中创建真实文件，测试结束后整体删除。
/// </summary>
public sealed class ExecutableLocatorTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录，存放测试用的程序与脚本。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-locator-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public ExecutableLocatorTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：在显式指定的、含空格的搜索目录中，按 <c>PATHEXT</c> 扩展名列表能找到不带扩展名指定的 <c>example.cmd</c>
    /// （Windows 上路径比较不区分大小写）；不存在的程序返回 <see langword="null"/>。PATHEXT 只在 Windows 上生效。
    /// </summary>
    [WindowsFact]
    public void FindsWindowsCommandShimsInPathsWithSpaces()
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
    public void PrefersNativeShimWhenExtensionlessScriptAlsoExists(bool absolutePath)
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
    public void RequiresExecutePermissionOnUnix()
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
    /// 验证：搜索路径中的相对条目（这里是相对于进程当前目录、确实存在且含有该程序的目录）与 <c>.</c> 一律被忽略，
    /// 只有同一目录以绝对路径出现时才能找到程序——相对条目等同于在当前目录中查找，会让不受信任的目录劫持程序名。
    /// </summary>
    [Fact]
    public void IgnoresRelativeSearchPathEntries()
    {
        var name = "crewterm-locator-relative-" + Guid.NewGuid().ToString("N");
        var relative = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, name)).FullName;
        try
        {
            var program = Path.Combine(relative, OperatingSystem.IsWindows() ? "example.exe" : "example");
            File.WriteAllText(program, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Null(ExecutableLocator.Find("example", string.Join(Path.PathSeparator, name, "."), ".EXE"));
            Assert.Equal(program, ExecutableLocator.Find("example", relative, ".EXE"), ignoreCase: OperatingSystem.IsWindows());
        }
        finally { Directory.Delete(relative, recursive: true); }
    }

    /// <summary>
    /// 删除本测试实例的临时根目录及其全部内容。
    /// </summary>
    public void Dispose() => Directory.Delete(root, true);
}
