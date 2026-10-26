namespace CrewTerm.Terminal.Emulation;

/// <summary>
/// VT 输出解析状态机的状态。
/// </summary>
internal enum VtParseState
{
    /// <summary>
    /// 普通文本：可打印字符写入屏幕，控制字符立即执行。
    /// </summary>
    Ground,

    /// <summary>
    /// 已读到 <c>ESC</c>，等待下一个字节决定序列类型。
    /// </summary>
    Escape,

    /// <summary>
    /// 控制序列（<c>ESC [</c>）中，收集参数直到结束字节。
    /// </summary>
    Csi,

    /// <summary>
    /// 字符串类序列（OSC、DCS、APC、PM）中，内容被忽略，直到 BEL 或 ST。
    /// </summary>
    String,

    /// <summary>
    /// 字符串序列中读到 <c>ESC</c>，若下一个字节是 <c>\</c> 则为 ST 结束符。
    /// </summary>
    StringEscape,

    /// <summary>
    /// 等待两字节转义序列的最后一个字节（字符集指定 <c>ESC ( 0</c>、<c>ESC % G</c> 等）。
    /// </summary>
    Charset
}
