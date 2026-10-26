using System.Text;
using System.Text.RegularExpressions;

namespace CrewTerm.Core.Security;

/// <summary>
/// 外部文本（会话名、状态原因、Agent 消息、任务与插件输出）在显示到工作台界面前的安全处理。
/// </summary>
/// <remarks>
/// 工作台本身运行在用户的终端里。外部文本若夹带 <c>ESC</c> 等控制字符，被界面原样输出时可能被宿主终端当作控制序列执行
/// （改标题、写剪贴板、伪造界面内容）；这些文本可能来自被提示注入的 Agent 或任意命令的输出。
/// daemon 在入口处拒绝标识类文本中的控制字符（<see cref="ContainsControl"/>），
/// 界面在显示长文本前再统一清理（<see cref="Sanitize"/>），两道防线互不依赖。
/// </remarks>
public static partial class DisplayText
{
    /// <summary>
    /// 清理后替换不可显示控制字符的符号。
    /// </summary>
    private const char Replacement = '�';

    /// <summary>
    /// 判断文本是否含有控制字符（C0、DEL、C1）。
    /// </summary>
    /// <param name="text">要检查的文本。</param>
    /// <param name="allowLineBreaks">为 <see langword="true"/> 时允许换行 <c>\n</c>、回车 <c>\r</c> 与制表符 <c>\t</c>。</param>
    /// <returns>含有不允许的控制字符时为 <see langword="true"/>。</returns>
    public static bool ContainsControl(string text, bool allowLineBreaks)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var character in text)
        {
            if (allowLineBreaks && character is '\n' or '\r' or '\t') continue;
            if (char.IsControl(character)) return true;
        }
        return false;
    }

    /// <summary>
    /// 去掉 ANSI 转义序列（颜色、光标移动、OSC 标题等），并把其余控制字符替换为 <c>U+FFFD</c>；保留换行与制表符。
    /// </summary>
    /// <remarks>
    /// 单独的回车 <c>\r</c>（进度条覆盖同一行）被删除，<c>\r\n</c> 保留为换行。
    /// </remarks>
    /// <param name="text">原始文本。</param>
    /// <returns>可以安全显示的文本。</returns>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withoutSequences = EscapeSequence().Replace(text, "");
        var result = new StringBuilder(withoutSequences.Length);
        foreach (var character in withoutSequences)
        {
            if (character is '\n' or '\t') result.Append(character);
            else if (character == '\r') continue;
            else if (char.IsControl(character)) result.Append(Replacement);
            else result.Append(character);
        }
        return result.ToString();
    }

    /// <summary>
    /// 匹配一个完整或被截断的转义序列：CSI（<c>ESC [ … 结束字节</c>）、以 BEL 或 ST 结束的 OSC/DCS/APC/PM/SOS 字符串，
    /// 以及其他两字节转义（<c>ESC</c> 加可选中间字节与结束字节）。
    /// </summary>
    /// <returns>可复用的正则实例。</returns>
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]?|[\]PX^_][^\x07\x1B]*(?:\x07|\x1B\\)?|[ -/]*[0-~]?)")]
    private static partial Regex EscapeSequence();
}
