using CrewTerm.Core.Editing;
using CrewTerm.Core.Projects;

namespace CrewTerm.Core.Tests.Projects;

/// <summary>
/// 覆盖 <see cref="WorkspaceNavigation"/> 的会话/项目切换规则，以及 <see cref="FileReferenceParser"/> 对编译器输出中文件位置的解析。
/// 均为纯内存单元测试，不访问真实文件。
/// </summary>
public sealed class NavigationTests
{
    /// <summary>
    /// 验证：依次选中属于不同项目的两个会话后，活动会话变为第二个会话，活动项目也随之切换到该会话所属的项目。
    /// </summary>
    [Fact]
    public void SwitchingSessionFollowsItsProject()
    {
        var state = new WorkspaceNavigation();
        Assert.True(state.TrySelectSession("session-1", "D:\\one"));
        Assert.True(state.TrySelectSession("session-2", "D:\\two"));
        Assert.Equal("D:\\two", state.ActiveProjectPath);
        Assert.Equal("session-2", state.ActiveSessionId);
    }

    /// <summary>
    /// 验证：编辑器存在未保存修改时，切换到其他项目的会话会被拒绝且活动会话保持不变；
    /// 只有显式传入 <c>discardConfirmed: true</c>（用户确认放弃修改）后才允许切换。
    /// </summary>
    [Fact]
    public void UnsavedBufferBlocksSilentProjectSwitch()
    {
        var state = new WorkspaceNavigation();
        state.TrySelectSession("first", "D:\\one");
        state.EditorDirty = true;
        Assert.False(state.TrySelectSession("second", "D:\\two"));
        Assert.Equal("first", state.ActiveSessionId);
        Assert.True(state.TrySelectSession("second", "D:\\two", discardConfirmed: true));
    }

    /// <summary>
    /// 验证：<c>文件:行:列</c>、MSBuild 风格的 <c>文件(行,列): error ...</c> 以及只有行号的 <c>文件:行</c> 三种格式，
    /// 都能解析为相对项目根目录的绝对路径和正确的行列号；缺少列号时列号取 1。
    /// </summary>
    /// <param name="text">待解析的编译器输出文本。</param>
    /// <param name="file">期望解析出的相对文件路径。</param>
    /// <param name="line">期望的行号。</param>
    /// <param name="column">期望的列号。</param>
    [Theory]
    [InlineData("src/Program.cs:12:4", "src/Program.cs", 12, 4)]
    [InlineData("src/Program.cs(7,3): error CS1000", "src/Program.cs", 7, 3)]
    [InlineData("src/Program.cs:9", "src/Program.cs", 9, 1)]
    public void CompilerLocationsResolveAgainstProject(string text, string file, int line, int column)
    {
        var root = Path.GetFullPath(".");
        var location = FileReferenceParser.Parse(text, root);
        Assert.NotNull(location);
        Assert.Equal(Path.GetFullPath(file, root), location.Path);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
    }

    /// <summary>
    /// 验证：<see cref="WorkspaceNavigation.RequiresDiscard"/> 只在编辑器有未保存内容且目标是另一个项目时为真；
    /// 同一项目（Windows 上大小写不同也算同一项目）或没有未保存内容时为假，且判断本身不改变当前项目。
    /// </summary>
    [Fact]
    public void DiscardIsRequiredOnlyWhenLeavingAProjectWithUnsavedEdits()
    {
        var state = new WorkspaceNavigation();
        Assert.True(state.TrySelectSession(null, @"D:\work\a"));
        Assert.False(state.RequiresDiscard(@"D:\work\b"));
        state.EditorDirty = true;
        Assert.True(state.RequiresDiscard(@"D:\work\b"));
        Assert.False(state.RequiresDiscard(@"D:\work\a"));
        if (OperatingSystem.IsWindows()) Assert.False(state.RequiresDiscard(@"D:\WORK\A"));
        Assert.Equal(@"D:\work\a", state.ActiveProjectPath);
    }
}
