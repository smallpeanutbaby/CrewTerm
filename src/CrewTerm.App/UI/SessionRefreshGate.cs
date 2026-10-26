namespace CrewTerm.App.UI;

/// <summary>Applied on the UI thread: responses captured before a user selection cannot replace it.</summary>
public sealed class SessionRefreshGate
{
    private long version;
    public long Capture() => Interlocked.Read(ref version);
    public void SelectionChanged() => Interlocked.Increment(ref version);
    public bool TryApply(long capturedVersion, Action update)
    {
        if (capturedVersion != Capture()) return false;
        update();
        return true;
    }
}
