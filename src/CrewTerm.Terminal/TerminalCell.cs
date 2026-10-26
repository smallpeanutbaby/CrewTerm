namespace CrewTerm.Terminal;

/// <summary>A display cell. Width zero marks the continuation of a wide glyph.</summary>
public sealed record TerminalCell(string Text, int Foreground, int Background, bool Bold, int Width = 1);
