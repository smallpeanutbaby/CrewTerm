namespace CrewTerm.Core.Editing;

/// <summary>
/// 从文本中解析出的文件位置。
/// </summary>
/// <param name="Path">绝对文件路径（相对路径已按项目根目录展开）。</param>
/// <param name="Line">行号，从 1 开始。</param>
/// <param name="Column">列号，从 1 开始；文本中未给出时为 1。</param>
public sealed record FileLocation(string Path, int Line, int Column);
