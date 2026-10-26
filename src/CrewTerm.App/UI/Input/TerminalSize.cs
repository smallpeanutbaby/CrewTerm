namespace CrewTerm.App.UI.Input;

/// <summary>
/// 终端尺寸（字符单元）。
/// </summary>
/// <param name="Columns">列数。</param>
/// <param name="Rows">行数。</param>
internal readonly record struct TerminalSize(int Columns, int Rows);
