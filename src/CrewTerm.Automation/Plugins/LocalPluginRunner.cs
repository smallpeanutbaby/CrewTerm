using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrewTerm.Automation.Tasks;

namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 执行插件清单中声明的本地进程：通过标准输入传入 JSON，输出有上限，支持超时与取消。
/// </summary>
/// <remarks>
/// <para>标准输入是一个 JSON 信封：<c>schemaVersion</c>（固定为 1）、<c>kind</c>（<c>command</c> 或 <c>event</c>）、
/// <c>eventName</c>、<c>pluginId</c>、<c>commandId</c> 与 <c>payload</c>，UTF-8 编码后不得超过 64 KiB。</para>
/// <para>这只是进程执行，不是安全沙箱：插件进程以当前用户身份运行，拥有与 CrewTerm 相同的权限，
/// 因此只应启用用户信任的插件。超时或取消时会结束整个进程树。</para>
/// </remarks>
public sealed class LocalPluginRunner
{
    /// <summary>
    /// 插件登记表；每次执行前都重新读取，保证使用最新的启用状态与清单。
    /// </summary>
    private readonly LocalPluginCatalog catalog;

    /// <summary>
    /// 并发槽位：同时运行的插件进程上限；等待槽位的时间也计入命令超时。
    /// </summary>
    private readonly SemaphoreSlim slots;

    /// <summary>
    /// 创建本地插件运行器。注意这只是进程执行，不是安全沙箱。
    /// </summary>
    /// <param name="catalog">本地插件登记表。</param>
    /// <param name="maxConcurrency">同时运行的插件进程上限，必须为正数。</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrency"/> 不是正数。</exception>
    public LocalPluginRunner(LocalPluginCatalog catalog, int maxConcurrency = 4)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        this.catalog = catalog;
        slots = new(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// 以 JSON 负载运行一个已启用插件中声明的命令。
    /// </summary>
    /// <param name="pluginId">插件标识（不区分大小写）。</param>
    /// <param name="commandId">已声明的命令标识（不区分大小写）。</param>
    /// <param name="payload">放入标准输入信封 <c>payload</c> 字段的 JSON 值；未初始化（<see cref="JsonValueKind.Undefined"/>）时写为 <c>null</c>。</param>
    /// <param name="ct">调用方取消令牌。</param>
    /// <returns>执行结果；插件未登记、已禁用、命令未声明、超时或启动失败等都以失败结果返回，不抛出异常。</returns>
    public Task<LocalPluginResult> RunAsync(string pluginId, string commandId, JsonElement payload, CancellationToken ct = default)
        => RunCoreAsync(pluginId, commandId, payload, null, ct);

    /// <summary>
    /// 并发运行所有已启用插件中订阅了该事件的钩子；单个插件的失败只体现在各自的结果中。
    /// </summary>
    /// <remarks>
    /// 事件名按序数精确匹配。读取登记表本身失败时，返回唯一一条插件标识为 <c>catalog</c>、命令标识为事件名的失败结果。
    /// </remarks>
    /// <param name="eventName">事件名，例如 <c>agent.state</c>、<c>session.exited</c>。</param>
    /// <param name="payload">放入标准输入信封 <c>payload</c> 字段的事件数据。</param>
    /// <param name="ct">调用方取消令牌，同时作用于所有钩子。</param>
    /// <returns>每个被触发钩子的结果；没有订阅者时为空列表。</returns>
    public async Task<IReadOnlyList<LocalPluginResult>> RunEventAsync(string eventName, JsonElement payload, CancellationToken ct = default)
    {
        try
        {
            var runs = catalog.List().Where(p => p.Enabled).SelectMany(p => (p.Manifest.Hooks ?? [])
                .Where(h => h.Event == eventName).Select(h => RunCoreAsync(p.Id, h.CommandId, payload, eventName, ct)));
            return await Task.WhenAll(runs).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return [new("catalog", eventName, false, null, "", error.Message, Cancelled: ct.IsCancellationRequested)]; }
    }

    /// <summary>
    /// 命令与事件钩子共用的执行流程：查找并检查插件与命令、构造标准输入信封、在超时内等待槽位并执行。
    /// </summary>
    /// <remarks>
    /// 超时计时从等待槽位之前开始，因此排队时间也计入 <see cref="LocalPluginCommand.TimeoutSeconds"/>。
    /// 除 <see cref="OutOfMemoryException"/> 外的所有异常都会转换为失败结果。
    /// </remarks>
    /// <param name="pluginId">插件标识。</param>
    /// <param name="commandId">命令标识。</param>
    /// <param name="payload">放入信封的 JSON 值。</param>
    /// <param name="eventName">事件名；直接运行命令时为 <see langword="null"/>，信封的 <c>kind</c> 随之为 <c>command</c>。</param>
    /// <param name="ct">调用方取消令牌。</param>
    /// <returns>执行结果，从不因执行失败而抛出异常。</returns>
    private async Task<LocalPluginResult> RunCoreAsync(string pluginId, string commandId, JsonElement payload, string? eventName, CancellationToken ct)
    {
        try
        {
            var plugin = catalog.Get(pluginId) ?? throw new KeyNotFoundException("Plugin not installed: " + pluginId);
            if (!plugin.Enabled) throw new InvalidOperationException("Plugin is disabled: " + pluginId);
            var command = plugin.Manifest.Commands.FirstOrDefault(c => c.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Plugin command not declared: " + commandId);
            var json = JsonSerializer.Serialize(new { schemaVersion = 1, kind = eventName is null ? "command" : "event", eventName,
                pluginId = plugin.Id, commandId = command.Id, payload = payload.ValueKind == JsonValueKind.Undefined ? (object?)null : payload });
            if (Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidDataException("Plugin stdin JSON is limited to 64 KiB.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
            try { await slots.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            { return Cancelled(pluginId, commandId, null, "", false, ct.IsCancellationRequested); }
            try { return await ExecuteAsync(plugin, command, json, deadline.Token, ct).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(pluginId, commandId, false, null, "", error.Message, Cancelled: ct.IsCancellationRequested); }
    }

    /// <summary>
    /// 启动插件进程（不经过 Shell），写入标准输入信封，收集输出，直到进程退出、超时或被取消。
    /// </summary>
    /// <remarks>
    /// <para>含目录分隔符的相对可执行文件路径和工作目录都会在启动前再次解析并检查是否逃出插件目录；
    /// 不含分隔符的程序名交给系统按 PATH 查找。</para>
    /// <para>标准输入使用无 BOM 的 UTF-8，写完即关闭；标准输出与标准错误并发读取，合并到有上限的缓冲中。
    /// 超时、取消或写入失败时结束整个进程树；结束后最多再等待 2 秒让进程退出。</para>
    /// </remarks>
    /// <param name="plugin">已启用的插件登记信息。</param>
    /// <param name="command">要执行的已声明命令。</param>
    /// <param name="json">已序列化的标准输入信封。</param>
    /// <param name="stop">超时或调用方取消时触发的令牌（已链接调用方令牌并设置了超时）。</param>
    /// <param name="caller">调用方原始令牌，用于区分“被取消”与“超时”。</param>
    /// <returns>执行结果。</returns>
    /// <exception cref="IOException"><see cref="Process.Start()"/> 返回 <see langword="false"/>，进程未能启动。</exception>
    /// <exception cref="InvalidDataException">相对路径逃出插件目录。</exception>
    /// <exception cref="OperationCanceledException">启动进程之前就已超时或被取消。</exception>
    private static async Task<LocalPluginResult> ExecuteAsync(LocalPluginInfo plugin, LocalPluginCommand command,
        string json, CancellationToken stop, CancellationToken caller)
    {
        var executable = command.EffectiveExecutable;
        if (!Path.IsPathRooted(executable) && executable.IndexOfAny(['/', '\\']) >= 0)
            executable = LocalPluginCatalog.ResolveRelative(plugin.SourceDirectory, executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = command.WorkingDirectory is null ? plugin.SourceDirectory : LocalPluginCatalog.ResolveRelative(plugin.SourceDirectory, command.WorkingDirectory),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.EffectiveArguments ?? []) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var output = new BoundedTextBuffer(command.MaxOutputCharacters);
        var gate = new object();
        stop.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Plugin process could not start.");
        // 结束整个进程树；进程已退出或已无法访问时静默忽略。
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        using var registration = stop.Register(Kill);
        // 读取一个输出流直到结束并追加到共享缓冲；因超时或取消而中断读取时不视为错误。
        async Task PumpAsync(StreamReader reader)
        {
            try
            {
                var buffer = new char[4096];
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory(), stop).ConfigureAwait(false)) > 0)
                    lock (gate) output.Append(new string(buffer, 0, length));
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or IOException) { }
        }
        var stdout = PumpAsync(process.StandardOutput);
        var stderr = PumpAsync(process.StandardError);
        Exception? executionError = null;
        try
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), stop).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Kill(); }
        catch (IOException) when (process.HasExited) { }
        catch (Exception error) when (error is not OutOfMemoryException) { executionError = error; Kill(); }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { executionError ??= error; Kill(); }
        }
        if (!process.HasExited)
        {
            Kill();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        int? exitCode = process.HasExited ? process.ExitCode : null;
        lock (gate)
        {
            if (stop.IsCancellationRequested) return Cancelled(plugin.Id, command.Id, exitCode, output.ToString(), output.Truncated, caller.IsCancellationRequested);
            if (executionError is not null) return new(plugin.Id, command.Id, false, exitCode, output.ToString(), executionError.Message,
                OutputTruncated: output.Truncated);
            return new(plugin.Id, command.Id, exitCode == 0, exitCode, output.ToString(), exitCode == 0 ? null : "Plugin exited with code " + exitCode,
                OutputTruncated: output.Truncated);
        }
    }

    /// <summary>
    /// 构造被取消或超时的失败结果。
    /// </summary>
    /// <param name="pluginId">插件标识。</param>
    /// <param name="commandId">命令标识。</param>
    /// <param name="exitCode">进程退出码；未启动或未退出时为 <see langword="null"/>。</param>
    /// <param name="output">已收集的输出。</param>
    /// <param name="truncated">输出是否被截断。</param>
    /// <param name="callerCancelled">为 <see langword="true"/> 表示调用方取消；否则视为超时。</param>
    /// <returns><see cref="LocalPluginResult.Cancelled"/> 与 <see cref="LocalPluginResult.TimedOut"/> 恰有一个为 <see langword="true"/> 的失败结果。</returns>
    private static LocalPluginResult Cancelled(string pluginId, string commandId, int? exitCode, string output, bool truncated, bool callerCancelled)
        => new(pluginId, commandId, false, exitCode, output, callerCancelled ? "Plugin execution cancelled." : "Plugin execution timed out.",
            TimedOut: !callerCancelled, OutputTruncated: truncated, Cancelled: callerCancelled);
}
