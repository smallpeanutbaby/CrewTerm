namespace CrewTerm.App.UI;

public sealed record PaletteCommand(string Id, string Label, Action Execute);

public static class CommandPalette
{
    public static IReadOnlyList<PaletteCommand> Filter(IEnumerable<PaletteCommand> commands, string query) =>
        commands.Where(command => command.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
}
