using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class AgentStateTrackerTests
{
    [Fact]
    public void OrdinaryLogsNeverImplySuccessfulTaskCompletion()
    {
        var state = new AgentStateTracker();
        state.ObserveOutput("Tests passed.\nAll done!\n");
        Assert.Equal(AgentState.Unknown, state.State);
        state.Report(AgentState.Working);
        state.ObserveOutput("finished writing file\n");
        Assert.Equal(AgentState.Working, state.State);
    }

    [Fact]
    public void VisibleApprovalPromptIsDetectedAcrossChunks()
    {
        var state = new AgentStateTracker();
        state.Report(AgentState.Working);
        state.ObserveOutput("Do you want to pro");
        state.ObserveOutput("ceed?\n1. Yes\n2. No");
        Assert.Equal(AgentState.NeedsInput, state.State);
    }

    [Fact]
    public void ExplicitReportOverridesPromptAndIgnoresStaleText()
    {
        var state = new AgentStateTracker();
        state.ObserveOutput("Do you want to proceed?");
        state.Report(AgentState.Working);
        state.ObserveOutput("Now applying the patch");
        Assert.Equal(AgentState.Working, state.State);
    }

    [Fact]
    public void ExitIsFinalUntilExplicitNewLifecycleReport()
    {
        var state = new AgentStateTracker();
        state.MarkExited(5);
        state.ObserveOutput("Do you want to proceed?");
        Assert.Equal(AgentState.Exited, state.State);
        Assert.Equal(5, state.ExitCode);
        state.Report(AgentState.Working);
        Assert.Null(state.ExitCode);
    }
}
