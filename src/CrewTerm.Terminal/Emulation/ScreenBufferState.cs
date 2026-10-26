namespace CrewTerm.Terminal.Emulation;

/// <summary>
/// 一块屏幕缓冲区的完整状态：单元格、光标位置与滚动区域。
/// </summary>
/// <remarks>
/// 切换到备用屏幕（<c>CSI ? 1049 h</c>）时保存主屏幕，切回时恢复；resize 时用于在新尺寸下重建缓冲区。
/// </remarks>
/// <param name="Cells">按行存储的单元格。</param>
/// <param name="Column">光标列（从 0 开始）。</param>
/// <param name="Row">光标行（从 0 开始）。</param>
/// <param name="Top">滚动区域首行（含）。</param>
/// <param name="Bottom">滚动区域末行（含）。</param>
internal sealed record ScreenBufferState(TerminalCell[][] Cells, int Column, int Row, int Top, int Bottom);
