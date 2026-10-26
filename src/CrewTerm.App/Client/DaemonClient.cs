using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Protocol;

namespace CrewTerm.App.Client;

/// <summary>
/// daemon 命名管道客户端：每次调用建立一个连接，发送一行 JSON 请求并读取一行 JSON 响应。
/// </summary>
/// <remarks>
/// 管道使用 <see cref="PipeOptions.CurrentUserOnly"/>，只有当前 Windows 用户可以连接。
/// 单次调用（连接 + 请求 + 响应）最长 15 秒；<c>agent-wait</c> 请求按其等待超时（最多 60 秒）再加 3 秒。
/// </remarks>
/// <param name="stateDirectory">状态目录；不同状态目录对应不同的 daemon 实例。</param>
public sealed class DaemonClient(string stateDirectory)
{
    /// <summary>
    /// 发送一个请求并等待响应。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="token">取消调用。</param>
    /// <returns>daemon 的响应；业务失败通过 <see cref="DaemonResponse.Ok"/> 与 <see cref="DaemonResponse.Error"/> 表示。</returns>
    /// <exception cref="TimeoutException">500 毫秒内无法连接（daemon 未运行或连接已满）。</exception>
    /// <exception cref="InvalidDataException">请求过大或响应无法解析。</exception>
    public async Task<DaemonResponse> CallAsync(DaemonRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Operation is "plugin-run" or "plugin-event" ? TimeSpan.FromSeconds(310) : request.Operation == "agent-wait"
            ? TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds, 1, 60000) + 3000)
            : TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName(stateDirectory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(500, timeout.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var json = JsonSerializer.Serialize(request, PipeProtocol.Json);
        if (json.Length > PipeProtocol.MaxRequest) throw new InvalidDataException("Input too large for one request");
        await writer.WriteLineAsync(json.AsMemory(), timeout.Token);
        var response = await PipeProtocol.ReadLineAsync(reader, PipeProtocol.MaxResponse, timeout.Token);
        return JsonSerializer.Deserialize<DaemonResponse>(response, PipeProtocol.Json)
            ?? throw new InvalidDataException("Invalid daemon response");
    }
}
