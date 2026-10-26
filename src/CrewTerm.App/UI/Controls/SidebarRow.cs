namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 侧栏中的一行：状态符号、名称与右侧的暗色说明。
/// </summary>
/// <param name="Key">行所代表对象的稳定标识（项目标识或会话标识）；单击与菜单按它找回对象，而不是按行号。</param>
/// <param name="Glyph">行首的状态符号（一列宽）。</param>
/// <param name="GlyphColor">状态符号的颜色（24 位 RGB）。</param>
/// <param name="Title">名称，例如项目名或会话名。</param>
/// <param name="Detail">右侧的暗色说明，例如 Git 分支或 <c>working · claude</c>；为空时不显示。</param>
/// <param name="Current">是否为当前项目或当前显示的会话；当前行的名称以粗体高亮。</param>
public sealed record SidebarRow(string Key, string Glyph, int GlyphColor, string Title, string Detail, bool Current);
