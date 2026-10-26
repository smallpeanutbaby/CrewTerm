using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Server;
using CrewTerm.App.Tests.Fixtures;

namespace CrewTerm.App.Tests.Cli;

/// <summary>
/// 版本与协议兼容性测试：以真实进程运行 <c>crewterm</c> 可执行文件、在测试进程内运行真实的 <see cref="DaemonHost"/>，或在命名管道上模拟 0.1.0 版旧 daemon，
/// 验证版本信息的展示、<c>ping</c> 响应中的版本与协议号，以及 CLI 连接旧 daemon 时的降级与提示行为。
/// </summary>
public sealed class VersionCompatibilityTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的状态目录（系统临时目录下的 <c>CrewTermVersionTests\&lt;GUID&gt;</c>），同时是 CLI 子进程的工作目录。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermVersionTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 被测 <c>crewterm</c> 可执行文件 的完整路径。
    /// </summary>
    private readonly string executable = CrewTermBuild.Executable;

    /// <summary>
    /// 在每个测试开始前创建状态目录。
    /// </summary>
    /// <returns>已完成的任务。</returns>
    public Task InitializeAsync() { Directory.CreateDirectory(state); return Task.CompletedTask; }

    /// <summary>
    /// 验证：<c>--help</c>、<c>--version</c>、<c>doctor</c> 都以退出码 0 输出当前版本 <c>CrewTerm</c> + <see cref="CrewTermVersion.Current"/>，且不会启动 daemon（不存在 <c>server.lock</c>）。
    /// </summary>
    /// <param name="command">要运行的子命令或全局选项。</param>
    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("doctor")]
    public async Task ExecutableAdvertisesCurrentReleaseWithoutStartingDaemon(string command)
    {
        var result = await RunAsync(command);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CrewTerm " + CrewTermVersion.Current, result.Output);
        Assert.False(File.Exists(Path.Combine(state, "server.lock")));
    }

    /// <summary>
    /// 验证：daemon 的 <c>ping</c> 响应仍在 <c>Text</c> 中保留旧客户端可识别的 <c>CrewTerm 版本号</c>，同时提供 <c>Version</c>（<see cref="CrewTermVersion.Current"/>）与 <c>ProtocolVersion</c>（<see cref="CrewTermVersion.ProtocolVersion"/>）字段；版本号取自项目文件，发版时无需修改测试。
    /// </summary>
    [Fact]
    public async Task PingAdvertisesVersionAndProtocolWithoutRemovingLegacyText()
    {
        using var stop = new CancellationTokenSource();
        using var host = new DaemonHost(state);
        var running = host.RunAsync(stop.Token);
        try
        {
            var response = await new DaemonClient(state).CallAsync(new("ping"));
            var json = JsonSerializer.SerializeToElement(response);
            Assert.Equal("CrewTerm " + CrewTermVersion.Current, response.Text);
            Assert.Equal(CrewTermVersion.Current, json.GetProperty("Version").GetString());
            Assert.Equal(CrewTermVersion.ProtocolVersion, json.GetProperty("ProtocolVersion").GetInt32());
        }
        finally { await stop.CancelAsync(); await running; }
    }

    /// <summary>
    /// 验证：连接只支持基础操作的 0.1.0 版旧 daemon 时，<c>sessions</c> 和 <c>read</c> 仍可用（退出码 0），并在标准错误中提示旧版本号与需要重启；
    /// 新命令 <c>agent-state</c> 以退出码 1 失败并提示重启，且根本不会发给旧 daemon；CLI 从不自动发送 <c>stop</c>，只有显式执行 <c>stop</c> 才会转发给旧 daemon。
    /// </summary>
    [Fact]
    public async Task LegacyDaemonKeepsReadListAndStopButNewCommandsExplainRestart()
    {
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<string>();
        var server = ServeLegacyAsync(requests, stop.Token);
        try
        {
            var listed = await RunAsync("sessions");
            Assert.Equal(0, listed.ExitCode);
            Assert.Contains("0.1.0", listed.Error);
            Assert.Contains("restart", listed.Error, StringComparison.OrdinalIgnoreCase);
            var read = await RunAsync("read", "legacy-session");
            Assert.Equal(0, read.ExitCode);
            Assert.Contains("LEGACY_SESSION_RETAINED", read.Output);
            var newer = await RunAsync("agent-state", "legacy-session");
            Assert.Equal(1, newer.ExitCode);
            Assert.Contains("restart", newer.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agent-state", requests);
            Assert.DoesNotContain("stop", requests);
            Assert.Equal(0, (await RunAsync("stop")).ExitCode);
            Assert.Contains("stop", requests);
        }
        finally { await stop.CancelAsync(); await server; }
    }

    /// <summary>
    /// 模拟 0.1.0 版旧 daemon：在状态目录对应的命名管道上逐个接受连接（单实例、仅限当前用户），以无 BOM 的 UTF-8 读取一行 JSON 请求并记录其操作名；
    /// <c>ping</c> 回复 <c>CrewTerm 0.1.0</c>，<c>list</c> 回复空会话列表，<c>screen</c> 回复固定文本 <c>LEGACY_SESSION_RETAINED</c>，<c>stop</c> 回复成功，
    /// 其他操作一律回复 <c>Unknown operation</c> 错误。令牌取消时安静退出。
    /// </summary>
    /// <param name="requests">按到达顺序记录收到的操作名。</param>
    /// <param name="token">停止模拟服务器的取消令牌。</param>
    /// <returns>模拟服务器的服务循环任务。</returns>
    private async Task ServeLegacyAsync(ConcurrentQueue<string> requests, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(PipeProtocol.PipeName(state), PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<DaemonRequest>((await reader.ReadLineAsync(token))!)!;
                requests.Enqueue(request.Operation);
                var response = request.Operation switch
                {
                    "ping" => new DaemonResponse(true, Text: "CrewTerm 0.1.0"),
                    "list" => new(true, Sessions: []),
                    "screen" => new(true, Text: "LEGACY_SESSION_RETAINED"),
                    "stop" => new(true),
                    _ => new(false, "Unknown operation: " + request.Operation)
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>
    /// 以状态目录为工作目录、并在参数前加上 <c>--state-dir</c> 运行一次被测可执行文件，以 UTF-8 收集标准输出和标准错误；15 秒内未结束则杀死进程树并重新抛出超时异常。
    /// </summary>
    /// <param name="args">子命令及其参数。</param>
    /// <returns>退出码、标准输出和标准错误。</returns>
    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = state,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--state-dir"); start.ArgumentList.Add(state);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>
    /// 测试结束后递归删除状态目录。
    /// </summary>
    /// <returns>已完成的任务。</returns>
    public Task DisposeAsync() => TestStateDirectory.DeleteAsync(state);
}
