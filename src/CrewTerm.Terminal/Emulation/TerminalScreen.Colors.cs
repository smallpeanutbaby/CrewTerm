namespace CrewTerm.Terminal.Emulation;

/// <remarks>
/// 本分部文件：SGR（选择图形属性）参数解析、16 色调色板、256 色索引换算与当前文字样式的设置和重置。
/// </remarks>
public sealed partial class TerminalScreen
{
    /// <summary>
    /// 把 SGR 参数串解析为数值序列，并把冒号分隔的子参数形式（ITU T.416）改写为等价的分号形式。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>以 <c>;</c> 分组；空参数或无效数字为 0，数值被限制在 0–100000。因此空参数串得到 <c>[0]</c>，即“重置”。</description></item>
    /// <item><description><c>4:n</c>（下划线样式）：<c>n</c> 为 0 时改写为 24（取消下划线），否则一律为 4（单下划线）。</description></item>
    /// <item><description><c>38:2:…</c> / <c>48:2:…</c> / <c>58:2:…</c>：取最后三个子参数作为 RGB，兼容带色彩空间编号的
    /// <c>38:2:CS:R:G:B</c> 与省略它的 <c>38:2:R:G:B</c>；<c>38:5:n</c> / <c>48:5:n</c> / <c>58:5:n</c> 取索引色。</description></item>
    /// <item><description>子参数不完整的颜色分组与其他带冒号的分组整体丢弃，不能让后面以分号分隔的参数被当作它的颜色分量。</description></item>
    /// </list>
    /// </remarks>
    /// <param name="parameters"><c>CSI</c> 与 <c>m</c> 之间的参数串（不含私有前缀）。</param>
    /// <returns>供 <see cref="ApplySgr"/> 顺序执行的数值。</returns>
    private static int[] ParseSgr(string parameters)
    {
        // 解析单个参数：空串或无效数字为 0，结果限制在 0–100000，防止溢出和超大参数。
        static int Number(string value) => int.TryParse(value, out var number) ? Math.Clamp(number, 0, 100000) : 0;
        var result = new List<int>();
        foreach (var group in parameters.Split(';'))
        {
            var parts = group.Split(':');
            var command = Number(parts[0]);
            if (parts.Length == 1) result.Add(command);
            else if (command == 4) result.Add(Number(parts[1]) == 0 ? 24 : 4);
            else if (command is 38 or 48 or 58)
            {
                var kind = Number(parts[1]);
                if (kind == 2 && parts.Length >= 5) result.AddRange([command, 2, .. parts.TakeLast(3).Select(Number)]);
                else if (kind == 5 && parts.Length >= 3) result.AddRange([command, 5, Number(parts[2])]);
            }
        }
        return result.ToArray();
    }
    /// <summary>
    /// 16 色调色板，24 位 RGB（<c>0xRRGGBB</c>）：索引 0–7 为标准色（SGR 30–37 / 40–47），
    /// 8–15 为高亮色（SGR 90–97 / 100–107）；同时也是 256 色索引 0–15 的颜色。
    /// </summary>
    private static readonly int[] Palette =
    [
        0x0C0C0C, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF
    ];

    /// <summary>
    /// 按顺序执行 SGR（选择图形属性）参数，更新当前颜色与文字样式。
    /// </summary>
    /// <remarks>
    /// 支持 0 重置；1/2/3/4/5/6/7/8/9 开启加粗、暗淡、斜体、下划线、闪烁、反显、隐藏、删除线（21 视同下划线）；
    /// 22/23/24/25/27/28/29 关闭对应样式（22 同时关闭加粗与暗淡）；30–37、90–97 与 40–47、100–107 设置调色板颜色；
    /// 39/49 恢复默认前景/背景色；<c>38;5;n</c> / <c>48;5;n</c> 设置 256 色索引色，<c>38;2;r;g;b</c> / <c>48;2;r;g;b</c>
    /// 设置真彩色（各分量限制在 0–255）。38/48 的子参数（类型及其后的色号或分量）无论是否完整都被一并跳过，
    /// 不会被当作独立的 SGR 代码；58（下划线颜色，屏幕模型不渲染）同样消费其子参数后忽略，59 忽略。其他值忽略。
    /// 反显不改变这里保存的颜色，而是在写入字符时交换前景与背景。调用方必须持有 <c>_gate</c>。
    /// </remarks>
    /// <param name="values"><see cref="ParseSgr"/> 产生的数值序列。</param>
    private void ApplySgr(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            switch (value)
            {
                case 0: ResetStyles(); break;
                case 1: _bold = true; break;
                case 2: _faint = true; break;
                case 3: _italic = true; break;
                case 4: case 21: _underline = true; break;
                case 5: case 6: _blink = true; break;
                case 8: _hidden = true; break;
                case 9: _strikethrough = true; break;
                case 22: _bold = _faint = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 25: _blink = false; break;
                case 28: _hidden = false; break;
                case 29: _strikethrough = false; break;
                case 7: _reverse = true; break;
                case 27: _reverse = false; break;
                case 39: _foreground = DefaultForeground; break;
                case 49: _background = DefaultBackground; break;
                case >= 30 and <= 37: _foreground = Palette[value - 30]; break;
                case >= 40 and <= 47: _background = Palette[value - 40]; break;
                case >= 90 and <= 97: _foreground = Palette[value - 90 + 8]; break;
                case >= 100 and <= 107: _background = Palette[value - 100 + 8]; break;
                case 38: case 48: case 58:
                    int? color = null;
                    var kind = i + 1 < values.Length ? values[i + 1] : -1;
                    if (kind == 5 && i + 2 < values.Length)
                        color = IndexedColor(Math.Clamp(values[i + 2], 0, 255));
                    else if (kind == 2 && i + 4 < values.Length)
                        color = Math.Clamp(values[i + 2], 0, 255) << 16
                            | Math.Clamp(values[i + 3], 0, 255) << 8 | Math.Clamp(values[i + 4], 0, 255);
                    // 扩展颜色的子参数无论是否完整都属于 38/48，必须一并跳过，不能被当作独立的 SGR 代码
                    // （例如被截断的 “38;5” 不能把 5 解释为闪烁）。
                    i += kind switch { 5 => 2, 2 => 4, -1 => 0, _ => 1 };
                    if (color is { } rgb) { if (value == 38) _foreground = rgb; else if (value == 48) _background = rgb; }
                    break;
            }
        }
    }

    /// <summary>
    /// 恢复默认图形属性（SGR 0）：默认前景色与背景色，关闭加粗、反显及全部文字样式。
    /// </summary>
    private void ResetStyles()
    {
        _foreground = DefaultForeground;
        _background = DefaultBackground;
        _bold = _reverse = _faint = _italic = _underline = _blink = _hidden = _strikethrough = false;
    }

    /// <summary>
    /// 把 xterm 256 色索引换算为 24 位 RGB。
    /// </summary>
    /// <remarks>
    /// 0–15 取 <see cref="Palette"/>；16–231 为 6×6×6 色立方，每个分量的级别为 0、95、135、175、215、255；
    /// 232–255 为 24 级灰阶，亮度从 8 起每级加 10（8–238）。
    /// </remarks>
    /// <param name="index">颜色索引，必须在 0–255 之间。</param>
    /// <returns><c>0xRRGGBB</c> 形式的颜色。</returns>
    private static int IndexedColor(int index)
    {
        if (index < 16) return Palette[index];
        if (index >= 232) { var gray = 8 + (index - 232) * 10; return gray << 16 | gray << 8 | gray; }
        var value = index - 16;
        // 色立方的单个分量：级别 0 为 0，级别 1–5 为 95、135、175、215、255。
        static int Component(int n) => n == 0 ? 0 : 55 + n * 40;
        return Component(value / 36) << 16 | Component(value / 6 % 6) << 8 | Component(value % 6);
    }
}
