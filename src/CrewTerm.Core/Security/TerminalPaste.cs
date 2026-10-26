using System.Text;

namespace CrewTerm.Core.Security;

/// <summary>
/// 把外部文本（剪贴板、编辑器选区、Agent 提示词）安全地送入终端输入的规则。
/// </summary>
/// <remarks>
/// bracketed paste（<c>ESC [ 200 ~</c> … <c>ESC [ 201 ~</c>）让终端程序把内容当作粘贴而不是按键。
/// 若内容本身含有 <c>ESC [ 201 ~</c>，粘贴会被提前结束，其后的文本（例如 <c>curl … | sh</c> 加回车）就会被当作命令执行；
/// 不受信任仓库中的文件、网页复制的文本都可能夹带这样的序列。因此送入终端前一律去掉 <c>ESC</c> 与 C1 控制字符。
/// </remarks>
public static class TerminalPaste
{
    /// <summary>
    /// bracketed paste 开始标记。
    /// </summary>
    public const string Start = "\u001b[200~";

    /// <summary>
    /// bracketed paste 结束标记。
    /// </summary>
    public const string End = "\u001b[201~";

    /// <summary>
    /// 去掉 <c>ESC</c> 与 C1 控制字符（<c>U+0080</c>–<c>U+009F</c>，其中 <c>U+009B</c> 是 8 位 CSI），
    /// 使文本无法提前结束 bracketed paste，也无法注入方向键、功能键等按键序列。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>不含上述字符的文本；其余字符（包括换行与制表符）保持不变。</returns>
    public static string RemoveEscapes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('\u001b') < 0 && !text.Any(c => c is >= '\u0080' and <= '\u009f')) return text;
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            if (character != '\u001b' && character is < '\u0080' or > '\u009f') result.Append(character);
        return result.ToString();
    }

    /// <summary>
    /// 去掉转义字符后用 bracketed paste 标记包裹。
    /// </summary>
    /// <param name="text">要粘贴的文本。</param>
    /// <returns>可直接写入终端输入的文本。</returns>
    public static string Bracketed(string text) => Start + RemoveEscapes(text) + End;

    /// <summary>
    /// 判断文本是否含有换行或回车：在未启用 bracketed paste 的终端中，它们会被当作 Enter 立即执行。
    /// </summary>
    /// <param name="text">要检查的文本。</param>
    /// <returns>含有 <c>\n</c> 或 <c>\r</c> 时为 <see langword="true"/>。</returns>
    public static bool HasLineBreak(string text) => text.AsSpan().IndexOfAny('\r', '\n') >= 0;
}
