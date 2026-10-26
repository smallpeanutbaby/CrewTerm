using CrewTerm.App.Protocol;
using CrewTerm.App.UI.Controls;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 覆盖 <see cref="AgentStateStyle"/>：会话状态到侧栏外观的映射、空间状态的汇总顺序，以及 Agent 行的程序名与说明。
/// </summary>
public sealed class AgentStateStyleTests
{
    /// <summary>
    /// 验证：各语义状态映射为 herdr 的外观；已完成但没有未读通知时按空闲显示；没有活动记录为未知；进程退出时总是显示已退出。
    /// </summary>
    [Fact]
    public void SessionStatesMapToHerdrStyleBadges()
    {
        Assert.Equal(AgentStateStyle.Blocked, AgentStateStyle.For(Session(AgentActivityState.Blocked)));
        Assert.Equal(AgentStateStyle.Working, AgentStateStyle.For(Session(AgentActivityState.Working)));
        Assert.Equal(AgentStateStyle.Done, AgentStateStyle.For(Session(AgentActivityState.Done, unread: 1)));
        Assert.Equal(AgentStateStyle.Idle, AgentStateStyle.For(Session(AgentActivityState.Done)));
        Assert.Equal(AgentStateStyle.Idle, AgentStateStyle.For(Session(AgentActivityState.Idle)));
        Assert.Equal(AgentStateStyle.Unknown, AgentStateStyle.For(Session(null)));
        Assert.Equal(AgentStateStyle.Exited, AgentStateStyle.For(Session(AgentActivityState.Working) with { HasExited = true, ExitCode = 0 }));
        Assert.Equal("◉", AgentStateStyle.Blocked.Glyph);
        Assert.Equal("○", AgentStateStyle.Idle.Glyph);
    }

    /// <summary>
    /// 验证：空间汇总取最需要关注的状态——等待决定优先于工作中，工作中优先于已完成未查看；没有会话时为 <see cref="AgentStateStyle.None"/>。
    /// </summary>
    [Fact]
    public void SpaceRollUpShowsTheMostUrgentAgent()
    {
        Assert.Equal(AgentStateStyle.Blocked, AgentStateStyle.RollUp([Session(AgentActivityState.Idle), Session(AgentActivityState.Working), Session(AgentActivityState.Blocked)]));
        Assert.Equal(AgentStateStyle.Working, AgentStateStyle.RollUp([Session(AgentActivityState.Done, unread: 2), Session(AgentActivityState.Working)]));
        Assert.Equal(AgentStateStyle.Done, AgentStateStyle.RollUp([Session(null), Session(AgentActivityState.Done, unread: 1)]));
        Assert.Equal(AgentStateStyle.None, AgentStateStyle.RollUp([]));
    }

    /// <summary>
    /// 验证：程序名优先取已识别的 Provider，否则取命令中程序文件名去掉扩展名的小写形式（支持带引号与空格的 Windows 路径以及 Unix 路径），
    /// 命令为空时为 <c>shell</c>；说明文本为“状态 · 程序”。
    /// </summary>
    /// <param name="command">会话命令。</param>
    /// <param name="provider">识别出的 Provider。</param>
    /// <param name="expected">期望的程序名。</param>
    [Theory]
    [InlineData("claude --model sonnet", "claude", "claude")]
    [InlineData("\"C:\\Program Files\\Tools\\Agent.EXE\" --flag", null, "agent")]
    [InlineData("cmd.exe /d /q", null, "cmd")]
    [InlineData("/bin/zsh -l", null, "zsh")]
    [InlineData("   ", null, "shell")]
    public void ProgramNameComesFromProviderOrCommand(string command, string? provider, string expected)
    {
        var session = Session(AgentActivityState.Working) with { Command = command, Provider = provider };
        Assert.Equal(expected, AgentStateStyle.Program(session));
        Assert.Equal("working · " + expected, AgentStateStyle.Describe(session));
    }

    /// <summary>
    /// 创建一个处于指定状态的测试会话。
    /// </summary>
    /// <param name="state">活动状态；为 <see langword="null"/> 表示没有活动记录。</param>
    /// <param name="unread">未读通知数。</param>
    /// <returns>会话信息。</returns>
    private static SessionInfo Session(AgentActivityState? state, int unread = 0) =>
        new("id", "agent", "claude", "/work", DateTimeOffset.UnixEpoch, false, null,
            state is { } value ? new AgentActivity("id", value, "test", "test", DateTimeOffset.UnixEpoch, 1) : null, unread);
}
