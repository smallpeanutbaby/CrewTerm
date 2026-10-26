using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CrewTerm.Core.IO;

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
    /// Unix 域套接字路径允许的最大字节数：macOS 的 <c>sun_path</c> 为 104 字节、Linux 为 108 字节（含结尾 NUL），留出余量。
    /// </summary>
    private const int MaxSocketPathBytes = 100;

    /// <summary>
    /// 请求与响应共用的 JSON 选项：属性名不区分大小写；非 ASCII 字符按 UTF-8 原样写出。
    /// </summary>
    /// <remarks>
    /// 默认编码器把每个中文字符写成 6 个字符的 <c>\uXXXX</c>，会让中文粘贴与提示词的实际上限缩小到约三分之一。
    /// 消息只在本机命名管道中传输、从不嵌入 HTML，因此可以使用宽松转义；旧版本按标准 JSON 解析，同样能读取。
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 计算状态目录对应的管道名。
    /// </summary>
    /// <remarks>
    /// <para>Windows：<c>CrewTerm-用户名-路径哈希前 24 位</c>（路径不区分大小写），与早期版本相同，可以连接已在运行的 daemon。</para>
    /// <para>Linux / macOS：命名管道是 Unix 域套接字，使用绝对路径——状态目录中的 <see cref="StateFiles.ServerSocket"/>。
    /// 它与锁文件位于同一目录，不受各个终端 <c>TMPDIR</c> 不同的影响，同一目录的不同写法（符号链接、大小写）也指向同一个套接字；
    /// 状态目录只允许当前用户访问，其他用户无法抢先占用这个路径。套接字路径不能超过约 104 字节，
    /// 状态目录过长时退回临时目录中的 <c>crewterm-哈希前 24 位</c>（用户名并入哈希）。</para>
    /// </remarks>
    /// <param name="stateDirectory">状态目录。</param>
    /// <returns>管道名（Windows 不含 <c>\\.\pipe\</c> 前缀）；Linux / macOS 上通常是套接字的绝对路径。</returns>
    public static string PipeName(string stateDirectory)
    {
        var path = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
            return "CrewTerm-" + Environment.UserName + "-" + key;
        }
        var socket = Path.Combine(path, StateFiles.ServerSocket);
        if (Encoding.UTF8.GetByteCount(socket) <= MaxSocketPathBytes) return socket;
        var identity = Encoding.UTF8.GetBytes(Environment.UserName + "|" + path);
        return "crewterm-" + Convert.ToHexString(SHA256.HashData(identity))[..24].ToLowerInvariant();
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
