namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 命令面板中的一条命令。
/// </summary>
/// <param name="Id">稳定的命令标识，例如 <c>file.save</c>，也参与过滤。</param>
/// <param name="Label">显示文本，例如 “Editor: Save file”。</param>
/// <param name="Execute">选中后在 UI 线程执行的操作。</param>
public sealed record PaletteCommand(string Id, string Label, Action Execute);
