using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Tests.Tasks;

/// <summary>
/// 覆盖 <see cref="TaskRunner"/> 中与继承环境有关的安全规则。测试会临时清除本进程的环境变量，因此属于
/// <see cref="ProcessEnvironmentCollection"/>，不与其他测试并行。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TaskRunnerEnvironmentTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时工作目录（名称带随机 GUID）。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-task-env-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建本测试实例使用的临时工作目录。
    /// </summary>
    public TaskRunnerEnvironmentTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证（Windows）：即使启动 CrewTerm 的环境没有设置 <c>NoDefaultCurrentDirectoryInExePath</c>，工作目录（项目目录）中的同名脚本
    /// 也不会劫持任务命令中的裸程序名——目录中放一个 <c>whoami.cmd</c>，运行 <c>whoami</c> 时执行的是 PATH 中的系统程序；
    /// 写成 <c>.\whoami.cmd</c> 时才运行项目中的脚本。与终端会话的规则一致：cmd 默认会先在当前目录中查找程序。
    /// </summary>
    [WindowsFact]
    public async Task BareProgramNamesAreNotResolvedFromTheWorkingDirectoryOnWindows()
    {
        const string variable = "NoDefaultCurrentDirectoryInExePath";
        var inherited = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            File.WriteAllText(Path.Combine(root, "whoami.cmd"), "@echo HIJACKED_BY_PROJECT\r\n");
            var bare = await new TaskRunner().RunAsync(new("bare", "whoami", root));
            Assert.True(bare.Succeeded, bare.Output);
            Assert.DoesNotContain("HIJACKED_BY_PROJECT", bare.Output);
            var explicitPath = await new TaskRunner().RunAsync(new("explicit", @".\whoami.cmd", root));
            Assert.Contains("HIJACKED_BY_PROJECT", explicitPath.Output);
        }
        finally { Environment.SetEnvironmentVariable(variable, inherited); }
    }

    /// <summary>
    /// 删除本测试实例的临时工作目录及其全部内容。
    /// </summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
