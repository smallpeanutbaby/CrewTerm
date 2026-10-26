namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 命令面板的过滤逻辑（与界面控件无关，便于单元测试）。
/// </summary>
public static class CommandPalette
{
    /// <summary>
    /// 按标识或显示文本做不区分大小写的子串过滤，保持原有顺序。
    /// </summary>
    /// <param name="commands">全部命令。</param>
    /// <param name="query">用户输入；空字符串匹配全部命令。</param>
    /// <returns>匹配的命令。</returns>
    public static IReadOnlyList<PaletteCommand> Filter(IEnumerable<PaletteCommand> commands, string query) =>
        commands.Where(command => command.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
}
