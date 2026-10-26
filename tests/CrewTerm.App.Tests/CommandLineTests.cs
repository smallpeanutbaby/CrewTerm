namespace CrewTerm.App.Tests;

public sealed class CommandLineTests
{
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

    [Fact]
    public void MissingOptionValueReportsActionableError()
    {
        Assert.Throws<ArgumentException>(() => CrewTermCommandLine.Parse(["new", "--cmd"]));
    }

    [Fact]
    public void NoCommandSelectsTerminalUi()
    {
        Assert.Equal("ui", CrewTermCommandLine.Parse([]).Command);
    }
}
