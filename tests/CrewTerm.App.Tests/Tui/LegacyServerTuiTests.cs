using System.Collections.Concurrent;
using System.Text.Json;
using CrewTerm.App.Protocol;
using CrewTerm.App.Tests.Fixtures;
using static CrewTerm.App.Tests.Tui.LayoutTuiTests;

namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 旧版 daemon 兼容性的端到端界面测试：在测试状态目录对应的命名管道上模拟一个只支持 <c>ping</c> 与 <c>list</c> 的 0.1.0 版 daemon，
/// 在伪终端中启动真实的工作区连接它，验证界面会明确提示服务器过旧，但绝不会替用户停止它。
/// </summary>
public sealed class LegacyServerTuiTests
{
    /// <summary>
    /// 验证：连接 0.1.0 版旧 daemon 时工作区显示 <c>OLD SERVER</c> 提示，F1 帮助中说明需要先完成或保存工作再重启；
    /// 执行旧服务器不支持的 <c>Agent: Notifications</c> 时，界面主区域给出安全升级说明，而不向服务器发送 <c>notifications</c> 请求；按 F10 分离退出时也不会发送 <c>stop</c>。
    /// </summary>
    [Fact]
    public async Task OlderServerIsVisibleInWorkspaceAndHelpWithoutStoppingIt()
    {
        var root = CrewTermBuild.Repository;
        var state = Path.Combine(Path.GetTempPath(), "CrewTermLegacyUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<string>();
        var server = ServeLegacy(state, requests, stop.Token);
        try
        {
            using var ui = Launch("legacy-ui", root, state);
            await Wait(() => ui.Screen.GetText().Contains("OLD SERVER"), ui, "visible compatibility notice");
            ui.Write("\u001bOP");
            await Wait(() => ui.Screen.GetText().Contains("SHORTCUTS") && ui.Screen.GetText().Contains("Finish or save"), ui, "restart explanation in help");
            ui.Write("\u001b");
            await Wait(() => !ui.Screen.GetText().Contains("SHORTCUTS"), ui, "help closed");
            await Command(ui, "Agent: Notifications");
            await Wait(() => string.Join('\n', ui.Screen.GetText().Split('\n').Skip(3).Take(42)).Contains("Finish or save"), ui, "unsupported action explains safe upgrade");
            Assert.DoesNotContain("notifications", requests);
            // 先确认提示框已经关闭再按 F10：模态框仍在时 F10 会被它吞掉，负载高时固定延时不够。
            ui.Write("\u001b");
            await Wait(() => !string.Join('\n', ui.Screen.GetText().Split('\n').Skip(3).Take(42)).Contains("Finish or save"), ui, "message closed");
            ui.Write("\u001b[21~");
            await Wait(() => ui.HasExited, ui, "detach from legacy server");
            Assert.DoesNotContain("stop", requests);
        }
        finally { await stop.CancelAsync(); await server; await TestStateDirectory.DeleteAsync(state); }
    }

    /// <summary>
    /// 模拟旧版 daemon：在状态目录对应的命名管道上逐个接受连接（单实例、仅限当前用户），每个连接读取一行 JSON 请求并记录其操作名；
    /// <c>ping</c> 回复版本文本 <c>CrewTerm 0.1.0</c>，<c>list</c> 回复空会话列表，其他操作一律回复 <c>Unknown operation</c> 错误。令牌取消时安静退出。
    /// </summary>
    /// <param name="state">daemon 状态目录，用于推导管道名。</param>
    /// <param name="requests">按到达顺序记录收到的操作名。</param>
    /// <param name="token">停止模拟服务器的取消令牌。</param>
    /// <returns>模拟服务器的服务循环任务。</returns>
    private static async Task ServeLegacy(string state, ConcurrentQueue<string> requests, CancellationToken token)
    {
        var listener = FakeDaemonPipe.Listen(state);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(token);
                await using var pipe = listener;
                listener = FakeDaemonPipe.Listen(state);
                try
                {
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    if (await reader.ReadLineAsync(token) is not { } line) continue;
                    var request = JsonSerializer.Deserialize<DaemonRequest>(line)!;
                    requests.Enqueue(request.Operation);
                    var response = request.Operation == "ping" ? new DaemonResponse(true, Text: "CrewTerm 0.1.0")
                        : request.Operation == "list" ? new(true, Sessions: []) : new(false, "Unknown operation: " + request.Operation);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), token);
                }
                // 客户端在收到回复前断开（例如界面分离时恰有一次轮询在途）：与真正的 daemon 一样忽略，继续接受下一个连接。
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await listener.DisposeAsync(); }
    }
}
