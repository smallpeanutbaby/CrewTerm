using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CrewTerm.App.Protocol;

/// <summary>
/// daemon 命名管道协议的公共约定：消息上限、JSON 选项与按行读取。
/// </summary>
/// <remarks>
/// 每个连接只交换一次：客户端写一行 JSON 请求（<see cref="DaemonRequest"/>），daemon 回一行 JSON 响应
/// （<see cref="DaemonResponse"/>），随后关闭连接。字段名不区分大小写；未知字段被忽略，
/// 新增的可选字段都带有与旧行为一致的默认值，使新旧版本的客户端与 daemon 可以互通。
/// </remarks>
public static class PipeProtocol
{
    /// <summary>
    /// 单个请求的最大字符数（128 Ki），足以容纳 60,000 字符的粘贴内容及其 JSON 转义。
    /// </summary>
    public const int MaxRequest = 131072;

    /// <summary>
    /// 单个响应的最大字符数（8 Mi），足以容纳最大尺寸屏幕快照与 2,000 行滚动历史。
    /// </summary>
    public const int MaxResponse = 8 * 1024 * 1024;

    /// <summary>
    /// 读取消息时每次从管道取出的字符数。
    /// </summary>
    private const int ReadBlockCharacters = 8192;

    /// <summary>
    /// 请求与响应共用的 JSON 选项：属性名不区分大小写；非 ASCII 字符按 UTF-8 原样写出。
    /// </summary>
    /// <remarks>
    /// 默认编码器把每个中文字符写成 6 个字符的 <c>\uXXXX</c>，会让中文粘贴与提示词的实际上限缩小到约三分之一。
    /// 消息只在本机命名管道中传输、从不嵌入 HTML，因此可以使用宽松转义；旧版本按标准 JSON 解析，同样能读取。
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 计算状态目录对应的管道名：<c>CrewTerm-用户名-路径哈希前 24 位</c>。
    /// </summary>
    /// <param name="stateDirectory">状态目录。</param>
    /// <returns>管道名（不含 <c>\\.\pipe\</c> 前缀）。</returns>
    public static string PipeName(string stateDirectory)
    {
        var path = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        return "CrewTerm-" + Environment.UserName + "-" + key;
    }

    /// <summary>
    /// 读取一条以 <c>\n</c> 结束的消息（去掉末尾的 <c>\r</c>）。
    /// </summary>
    /// <remarks>
    /// 按块读取而不是逐字符读取：屏幕快照响应可达数百 KB，逐字符的异步读取会产生同样数量的状态机调用。
    /// 协议每个连接只有一条消息，换行之后的内容直接丢弃。
    /// </remarks>
    /// <param name="reader">管道上的 UTF-8 读取器。</param>
    /// <param name="maximum">允许的最大字符数（不含换行）。</param>
    /// <param name="token">取消读取。</param>
    /// <returns>消息文本。</returns>
    /// <exception cref="InvalidDataException">消息超过 <paramref name="maximum"/>。</exception>
    /// <exception cref="EndOfStreamException">对端在发送完整消息前关闭了连接。</exception>
    public static async Task<string> ReadLineAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[ReadBlockCharacters];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (text.Length + length > maximum) throw new InvalidDataException("Protocol message exceeds maximum size");
            text.Append(buffer, 0, length);
            if (newline >= 0) return text.ToString().TrimEnd('\r');
        }
        throw new EndOfStreamException("Daemon connection ended before a complete response");
    }
}
