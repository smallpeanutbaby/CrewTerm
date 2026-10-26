namespace CrewTerm.Core;

public sealed class WorkspaceNavigation
{
    public string? ActiveProjectPath { get; private set; }
    public string? ActiveSessionId { get; private set; }
    public bool EditorDirty { get; set; }

    public bool TrySelectSession(string? sessionId, string projectPath, bool discardConfirmed = false)
    {
        var sameRoot = string.Equals(ActiveProjectPath, projectPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (EditorDirty && !sameRoot && !discardConfirmed) return false;
        if (!sameRoot) EditorDirty = false;
        ActiveSessionId = sessionId;
        ActiveProjectPath = projectPath;
        return true;
    }
}
