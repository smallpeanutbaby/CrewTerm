namespace CrewTerm.App.Tests.Cli;

/// <summary>
/// <see cref="CrewTermCommandLine.Parse"/> 的单元测试：全局选项与子命令选项的解析、缺少选项值时的报错，以及没有子命令时的默认命令。
/// </summary>
public sealed class CommandLineTests
{
    /// <summary>
    /// 验证：子命令识别为 <c>new</c>，全局选项 <c>--state-dir</c> 和子命令之后各选项的取值（含空格、中文路径和带参数的命令行）都被原样保留。
    /// </summary>
    [Fact]
    public void GlobalAndCommandArgumentsRemainLiteral()
    {
        var options = CrewTermCommandLine.Parse(["--state-dir", "D:\\state space", "new", "--cwd", "D:\\代码\\a", "--cmd", "cmd.exe /k echo hello", "--name", "My agent"]);
        Assert.Equal("new", options.Command);
        Assert.Equal("D:\\state space", options.StateDirectory);
        Assert.Equal("D:\\代码\\a", options.Get("cwd"));
        Assert.Equal("cmd.exe /k echo hello", options.Get("cmd"));
        Assert.Equal("My agent", options.Get("name"));
    }

    /// <summary>
    /// 验证：选项 <c>--cmd</c> 位于末尾、缺少取值时，解析抛出 <see cref="ArgumentException"/>。
    /// </summary>
    [Fact]
    public void MissingOptionValueReportsActionableError()
    {
        Assert.Throws<ArgumentException>(() => CrewTermCommandLine.Parse(["new", "--cmd"]));
    }

    /// <summary>
    /// 验证：不带任何参数时子命令为 <c>ui</c>，即启动终端界面。
    /// </summary>
    [Fact]
    public void NoCommandSelectsTerminalUi()
    {
        Assert.Equal("ui", CrewTermCommandLine.Parse([]).Command);
    }
}
