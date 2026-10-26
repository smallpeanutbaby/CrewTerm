using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.App.Server;

/// <summary>
/// daemon 内的本地插件服务：插件登记表、按需执行、后台事件队列与最近的执行历史。
/// </summary>
/// <remarks>
/// daemon 内部事件（Agent 状态变化、会话创建/退出、收到消息）通过 <see cref="Queue"/> 进入有界队列，
/// 由一个后台任务按顺序交给订阅它们的插件，不会阻塞请求处理。所有成员都可以从任意线程调用。
/// 插件进程以当前用户身份运行，不是安全沙箱。
/// </remarks>
internal sealed class DaemonPluginService : IDisposable
{
    /// <summary>
    /// 执行插件进程的运行器（默认最多同时运行 4 个插件进程）。
    /// </summary>
    private readonly LocalPluginRunner runner;

    /// <summary>
    /// 停止信号：<see cref="StopAsync"/> 时取消，结束后台事件消费并中止所有正在执行的插件进程。
    /// </summary>
    private readonly CancellationTokenSource stop = new();

    /// <summary>
    /// 最近最多 100 条执行结果（按需运行、事件触发以及队列溢出的记录），最旧的先被丢弃。
    /// </summary>
    private readonly ConcurrentQueue<LocalPluginResult> history = new();

    /// <summary>
    /// 等待后台执行的事件（事件名与 JSON 负载），容量 32；队列已满时新事件被丢弃并记录一条失败结果，而不是阻塞调用方。
    /// </summary>
    private readonly Channel<(string Name, JsonElement Payload)> events = Channel.CreateBounded<(string, JsonElement)>(32);

    /// <summary>
    /// 按顺序消费 <see cref="events"/> 的后台任务（见 <see cref="ConsumeAsync"/>）。
    /// </summary>
    private readonly Task worker;

    /// <summary>
    /// 是否已调用 <see cref="Dispose"/>。
    /// </summary>
    private bool disposed;

    /// <summary>
    /// 本地插件登记表（<c>local-plugins.json</c>），供 daemon 列出、安装、启用与移除插件。
    /// </summary>
    internal LocalPluginCatalog Catalog { get; }

    /// <summary>
    /// 打开状态目录中的插件登记表并启动后台事件消费任务。
    /// </summary>
    /// <param name="stateDirectory">状态目录。</param>
    internal DaemonPluginService(string stateDirectory)
    {
        Catalog = new(stateDirectory);
        runner = new(Catalog);
        worker = ConsumeAsync();
    }

    /// <summary>
    /// 返回最近执行结果的快照，最旧的在前。
    /// </summary>
    /// <returns>最多 100 条结果。</returns>
    internal LocalPluginResult[] History() => history.ToArray();

    /// <summary>
    /// 按需运行一个已启用插件中声明的命令，并把结果记入历史。
    /// </summary>
    /// <param name="pluginId">插件标识。</param>
    /// <param name="commandId">插件清单中声明的命令标识。</param>
    /// <param name="payload">传给插件的 JSON 负载；为 <see langword="default"/> 时写为 <c>null</c>。</param>
    /// <param name="token">调用方取消；服务停止时也会取消。</param>
    /// <returns>执行结果；插件未登记、已禁用、命令未声明、超时等都以失败结果表示。</returns>
    internal async Task<LocalPluginResult> RunAsync(string pluginId, string commandId, JsonElement payload, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
        var result = await runner.RunAsync(pluginId, commandId, payload, linked.Token);
        Remember(result);
        return result;
    }

    /// <summary>
    /// 立即运行所有订阅该事件的已启用插件钩子，并把每个结果记入历史。
    /// </summary>
    /// <param name="eventName">事件名。</param>
    /// <param name="payload">传给插件的 JSON 负载。</param>
    /// <param name="token">调用方取消；服务停止时也会取消。</param>
    /// <returns>每个被触发钩子的结果；没有订阅者时为空数组。</returns>
    internal async Task<LocalPluginResult[]> RunEventAsync(string eventName, JsonElement payload, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
        var results = (await runner.RunEventAsync(eventName, payload, linked.Token)).ToArray();
        foreach (var result in results) Remember(result);
        return results;
    }

    /// <summary>
    /// 把事件放入后台队列后立即返回，不等待插件执行。
    /// </summary>
    /// <remarks>
    /// 服务停止后调用被忽略；队列已满（32 个）时丢弃该事件，并以插件标识 <c>daemon</c> 记录一条失败结果。
    /// </remarks>
    /// <param name="eventName">事件名。</param>
    /// <param name="payload">传给插件的 JSON 负载。</param>
    internal void Queue(string eventName, JsonElement payload)
    {
        if (stop.IsCancellationRequested) return;
        if (!events.Writer.TryWrite((eventName, payload)))
            Remember(new("daemon", eventName, false, null, "", "Plugin event queue is full; this event was not executed"));
    }

    /// <summary>
    /// 后台循环：按入队顺序逐个执行事件，前一个事件的全部钩子结束后才处理下一个；服务停止时退出，剩余事件不再执行。
    /// </summary>
    /// <returns>服务停止后正常完成的任务。</returns>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(stop.Token))
                await RunEventAsync(item.Name, item.Payload, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>
    /// 把一条结果加入历史，并把历史裁剪到最近 100 条。
    /// </summary>
    /// <param name="result">执行结果。</param>
    private void Remember(LocalPluginResult result)
    {
        history.Enqueue(result);
        while (history.Count > 100) history.TryDequeue(out _);
    }

    /// <summary>
    /// 停止服务：不再接受新事件，取消正在执行的插件与尚未执行的事件，并等待后台任务结束。可以重复调用。
    /// </summary>
    /// <returns>后台任务结束后完成的任务。</returns>
    internal async Task StopAsync()
    {
        events.Writer.TryComplete();
        // daemon 运行期间被释放时，Dispose 已经停止服务并释放了停止信号；之后 RunAsync 收尾时再次调用只需等待后台任务。
        try { await stop.CancelAsync(); }
        catch (ObjectDisposedException) { }
        await worker;
    }

    /// <summary>
    /// 同步停止服务（见 <see cref="StopAsync"/>）并释放停止信号；重复调用无效果。
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAsync().GetAwaiter().GetResult();
        stop.Dispose();
    }
}
