namespace CrewTerm.Core.Layouts;

/// <summary>
/// 以终端单元格为单位的矩形。
/// </summary>
/// <param name="X">左上角所在列（从 0 开始，相对于整个窗格区域）。</param>
/// <param name="Y">左上角所在行（从 0 开始，相对于整个窗格区域）。</param>
/// <param name="Width">宽度（单元格，可以为 0）。</param>
/// <param name="Height">高度（单元格，可以为 0）。</param>
public sealed record LayoutRect(int X, int Y, int Width, int Height);
