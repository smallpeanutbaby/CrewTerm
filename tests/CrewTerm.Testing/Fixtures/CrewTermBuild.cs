using CrewTerm.Testing.Platform;

namespace CrewTerm.Testing.Fixtures;

/// <summary>
/// 定位源码仓库与被测的 CrewTerm 可执行文件。
/// </summary>
/// <remarks>
/// 设置环境变量 <c>CREWTERM_TEST_EXE</c> 时测试发布产物；否则测试与测试程序集相同构建配置下的
/// <c>src/CrewTerm.App/bin/&lt;配置&gt;/net10.0/</c> 中的可执行文件。
/// </remarks>
public static class CrewTermBuild
{
    /// <summary>
    /// 从测试输出目录向上查找包含 <c>CrewTerm.slnx</c> 的仓库根目录。
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">一直查到磁盘根目录仍未找到。</exception>
    public static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrewTerm.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("CrewTerm source repository not found");
        }
    }

    /// <summary>
    /// 被测可执行文件的完整路径。
    /// </summary>
    public static string Executable
    {
        get
        {
            var published = Environment.GetEnvironmentVariable("CREWTERM_TEST_EXE");
            if (!string.IsNullOrWhiteSpace(published)) return Path.GetFullPath(published);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            return Path.Combine(Repository, "src", "CrewTerm.App", "bin", configuration, "net10.0", TestShell.CrewTermFileName);
        }
    }
}
