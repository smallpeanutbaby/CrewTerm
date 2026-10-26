using CrewTerm.App.UI;

namespace CrewTerm.App.Tests;

public sealed class SessionRefreshGateTests
{
    [Fact]
    public async Task DelayedRefreshCannotUndoNewUserSessionSelection()
    {
        var gate = new SessionRefreshGate();
        var selected = "old-agent";
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = gate.Capture();
        async Task CompleteOldRequest()
        {
            await response.Task;
            gate.TryApply(version, () => selected = "old-agent");
        }
        var request = CompleteOldRequest();
        selected = "new-agent";
        gate.SelectionChanged();
        response.SetResult();
        await request;
        Assert.Equal("new-agent", selected);
        Assert.True(gate.TryApply(gate.Capture(), () => selected = "current-agent"));
        Assert.Equal("current-agent", selected);
    }
}
