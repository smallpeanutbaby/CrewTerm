namespace CrewTerm.Terminal;

public sealed record TerminalSnapshot(
    int Columns, int Rows, int CursorColumn, int CursorRow, bool CursorVisible,
    TerminalCell[][] Cells, string[] History, bool AlternateScreen,
    bool BracketedPaste = false, bool ApplicationCursorKeys = false, int MouseTracking = 0, bool SgrMouse = false);
