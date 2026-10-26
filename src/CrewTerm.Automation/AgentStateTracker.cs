using System.Text.RegularExpressions;

namespace CrewTerm.Automation;

public enum AgentState { Unknown, Working, NeedsInput, Exited }

/// <summary>Conservative screen hint tracking. A hint is never permission to answer an approval prompt.</summary>
public sealed partial class AgentStateTracker
{
    private readonly object gate = new();
    private string recent = "";
    private AgentState state;
    private int? exitCode;

    public AgentState State { get { lock (gate) return state; } }
    public int? ExitCode { get { lock (gate) return exitCode; } }

    public void ObserveOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (gate)
        {
            if (state == AgentState.Exited) return;
            recent += output;
            if (recent.Length > 2048) recent = recent[^2048..];
            if (ApprovalPrompt().IsMatch(AnsiControl().Replace(recent, ""))) state = AgentState.NeedsInput;
        }
    }

    public void Report(AgentState reported)
    {
        if (!Enum.IsDefined(reported)) throw new ArgumentOutOfRangeException(nameof(reported));
        lock (gate)
        {
            state = reported;
            recent = "";
            exitCode = null;
        }
    }

    public void MarkExited(int? code)
    {
        lock (gate)
        {
            state = AgentState.Exited;
            exitCode = code;
            recent = "";
        }
    }

    [GeneratedRegex(@"(?im)^\s*(?:do you want to proceed\?|would you like to proceed\?|allow this command\?)")]
    private static partial Regex ApprovalPrompt();

    [GeneratedRegex("\u001b\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiControl();
}
