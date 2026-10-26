namespace CrewTerm.Core.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void SwitchingSessionFollowsItsProject()
    {
        var state = new WorkspaceNavigation();
        Assert.True(state.TrySelectSession("session-1", "D:\\one"));
        Assert.True(state.TrySelectSession("session-2", "D:\\two"));
        Assert.Equal("D:\\two", state.ActiveProjectPath);
        Assert.Equal("session-2", state.ActiveSessionId);
    }

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
}
