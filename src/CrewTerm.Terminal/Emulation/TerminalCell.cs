using System.Text.Json.Serialization;

namespace CrewTerm.Terminal.Emulation;

/// <summary>
/// 屏幕上的一个字符单元格：显示文本、颜色与文字样式。
/// </summary>
/// <remarks>
/// 宽字符（中日韩文字、emoji）占两个单元格：第一个单元格 <see cref="Width"/> 为 2 并保存完整字形，
/// 紧随其后的续位单元格 <see cref="Width"/> 为 0、文本为空。
/// 布尔样式为默认值 <see langword="false"/> 时不写入 JSON：屏幕快照每 160 毫秒经命名管道传输一次，
/// 省略这些字段可使快照体积减少一半以上；缺失的字段反序列化后仍为 <see langword="false"/>，与旧版本兼容。
/// </remarks>
/// <param name="Text">单元格显示的字形（可能包含组合字符）；续位单元格为空字符串。</param>
/// <param name="Foreground">前景色，24 位 RGB（<c>0xRRGGBB</c>）。</param>
/// <param name="Background">背景色，24 位 RGB（<c>0xRRGGBB</c>）。</param>
/// <param name="Bold">是否加粗。</param>
/// <param name="Width">显示宽度：1 为普通字符，2 为宽字符首格，0 为宽字符的续位单元格。</param>
/// <param name="Faint">是否暗淡（SGR 2）。</param>
/// <param name="Italic">是否斜体（SGR 3）。</param>
/// <param name="Underline">是否下划线（SGR 4）。</param>
/// <param name="Blink">是否闪烁（SGR 5/6）。</param>
/// <param name="Hidden">是否隐藏（SGR 8），绘制时以背景色显示。</param>
/// <param name="Strikethrough">是否删除线（SGR 9）。</param>
public sealed record TerminalCell(string Text, int Foreground, int Background,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Bold,
    int Width = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Faint = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Italic = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Underline = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Blink = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Strikethrough = false);
