using System.Text.Json;
using CrewTerm.App.UI.Controls;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：已保存会话的恢复、重启、绑定与遗忘，以及本地插件的安装、启用、查看、移除、运行与执行历史。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 从已保存的会话记录（只剩记录或进程已退出的会话）中选择一个，恢复原始对话或以保存的命令重新启动，并显示到活动面板。
    /// </summary>
    /// <remarks>
    /// 必要时先切换到记录所属的项目（用户可能取消）。成功后活动面板改为显示新会话并获得焦点，
    /// 绑定写回布局，并记为下次恢复的会话。
    /// </remarks>
    /// <param name="resume">
    /// 为 <see langword="true"/> 时精确恢复 Agent 的原始对话（需要已知原生会话标识或恢复命令）；
    /// 为 <see langword="false"/> 时用保存的命令开始一个新对话。
    /// </param>
    /// <exception cref="InvalidOperationException">选择恢复但该记录无法精确恢复（消息包含原因与绑定提示），或 daemon 调用失败。</exception>
    private void RestoreSavedSession(bool resume)
    {
        var saved = (Call(new("session-catalog")).Sessions ?? []).Where(s => s.IsDormant || s.HasExited).ToArray();
        var choice = Dialogs.Choose(application, resume ? "RESUME CONVERSATION" : "RESTART AS NEW CONVERSATION",
            saved.Select(s => $"{s.Name}  ·  {s.Provider ?? "custom"}  ·  {(resume ? s.CanResume ? "ready" : s.ResumeReason : s.Command)}").ToArray());
        if (choice < 0) return;
        var record = saved[choice];
        if (resume && !record.CanResume) throw new InvalidOperationException(record.ResumeReason + "\nUse Agent: Bind native conversation / resume command.");
        if (!BindSessionProject(record.WorkingDirectory)) return;
        // 新进程直接以当前面板尺寸启动；同一会话标识以前确认过的尺寸已不适用于新进程，清掉后下次布局会重新发送。
        var pane = ActiveTerminal;
        var response = Call(new(resume ? "session-resume" : "session-restart", record.Id,
            Columns: Math.Clamp(pane.Viewport.Width, 2, 300), Rows: Math.Clamp(pane.Viewport.Height, 2, 120)));
        resizeCoordinator.Forget(record.Id);
        refreshGate.SelectionChanged();
        ActiveTerminal.SessionId = response.SessionId; ActiveTerminal.Update(null); ActiveTerminal.SetFocus();
        terminalLayout.SaveBindings(); settings.LastSessionId = response.SessionId;
        status.Text = (resume ? "Resumed original conversation: " : "Started new conversation: ") + record.Name;
    }

    /// <summary>
    /// 为一条会话记录绑定原生对话标识或显式的恢复命令，使之后可以精确恢复原始对话。
    /// </summary>
    /// <remarks>
    /// 先询问提供方：<c>claude</c>、<c>codex</c>、<c>gemini</c>、<c>opencode</c>、<c>copilot</c>（不区分大小写）
    /// 需要输入确切的原生对话标识（不能用 “latest” 或部分前缀）；其他提供方需要输入能恢复这一对话的完整命令。
    /// 任何一步取消或留空都不会修改记录。
    /// </remarks>
    private void BindNativeSession()
    {
        var saved = Call(new("session-catalog")).Sessions ?? [];
        var choice = Dialogs.Choose(application, "Bind saved conversation", saved.Select(s => $"{s.Name}  ·  {s.Provider ?? "custom"}  ·  {s.Id}").ToArray());
        if (choice < 0) return;
        var record = saved[choice];
        var provider = Dialogs.Prompt(application, "Conversation provider", "claude / codex / gemini / opencode / copilot / custom", record.Provider ?? "custom");
        if (string.IsNullOrWhiteSpace(provider)) return;
        string? nativeId = null, command = null;
        if (provider.Trim().ToLowerInvariant() is "claude" or "codex" or "gemini" or "opencode" or "copilot")
            nativeId = Dialogs.Prompt(application, "Exact conversation ID", "Native ID (never latest or a partial prefix)", record.NativeSessionId ?? "");
        else command = Dialogs.Prompt(application, "Explicit resume command", "Command which resumes this exact conversation", "");
        if (string.IsNullOrWhiteSpace(nativeId) && string.IsNullOrWhiteSpace(command)) return;
        Call(new("session-bind", record.Id, Provider: provider.Trim(), NativeSessionId: nativeId, ResumeCommand: command));
        status.Text = "Native conversation bound: " + record.Name;
    }

    /// <summary>
    /// 删除一条只剩记录或进程已退出的会话记录；Agent 提供方自己保存的对话历史不受影响。
    /// </summary>
    private void ForgetSession()
    {
        var saved = (Call(new("session-catalog")).Sessions ?? []).Where(s => s.IsDormant || s.HasExited).ToArray();
        var choice = Dialogs.Choose(application, "Forget dormant record (provider history remains)", saved.Select(s => s.Name + "  ·  " + s.Id).ToArray());
        if (choice < 0) return;
        Call(new("session-forget", saved[choice].Id));
        status.Text = "Forgot session record: " + saved[choice].Name;
    }

    /// <summary>
    /// 从 daemon 读取已登记的本地插件。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Call"/> 一样在线程池上发起调用后同步等待，避免阻塞 UI 同步上下文造成死锁。
    /// </remarks>
    /// <param name="tolerateOlderServer">
    /// 为 <see langword="true"/> 时，daemon 版本较旧或不认识 <c>plugin-list</c> 操作都返回空数组而不是抛出异常（命令面板使用）。
    /// </param>
    /// <returns>已登记的插件；没有插件时为空数组。</returns>
    /// <exception cref="InvalidOperationException">daemon 版本较旧或返回失败，且 <paramref name="tolerateOlderServer"/> 为 <see langword="false"/>。</exception>
    private LocalPluginInfo[] ReadPlugins(bool tolerateOlderServer = false)
    {
        if (serverCompatibilityWarning is not null)
        {
            if (tolerateOlderServer) return [];
            throw new InvalidOperationException(serverCompatibilityWarning);
        }
        var response = Task.Run(() => client.CallAsync(new("plugin-list"), lifetime.Token)).GetAwaiter().GetResult();
        if (!response.Ok)
        {
            if (tolerateOlderServer && response.Error?.StartsWith("Unknown operation", StringComparison.Ordinal) == true) return [];
            throw new InvalidOperationException(response.Error);
        }
        return response.Plugins ?? [];
    }

    /// <summary>
    /// 为每个已启用插件的每条命令生成命令面板条目。
    /// </summary>
    /// <remarks>
    /// 插件列表在调用时立即读取（连接旧版 daemon 时为空）；返回的序列是延迟求值的投影，每次枚举都会重新生成条目。
    /// </remarks>
    /// <returns>标识为 <c>plugin.插件标识.命令标识</c>、显示为 “Plugin: 插件名 / 命令名” 的命令，选中后运行该插件命令。</returns>
    private IEnumerable<PaletteCommand> PluginCommands() => ReadPlugins(true).Where(p => p.Enabled).SelectMany(plugin =>
        plugin.Manifest.Commands.Select(command => new PaletteCommand("plugin." + plugin.Id + "." + command.Id,
            "Plugin: " + plugin.Name + " / " + command.Name, () => RunPlugin(plugin, command))));

    /// <summary>
    /// 登记一个本地插件目录（其中包含 <c>crewterm-plugin.json</c>），登记后默认处于禁用状态，需要查看清单后再启用。
    /// </summary>
    /// <remarks>
    /// 输入的路径会去掉首尾空白与引号；取消或留空时什么也不做。
    /// </remarks>
    /// <exception cref="InvalidOperationException">daemon 版本较旧，或清单无效等原因导致登记失败。</exception>
    private void InstallPlugin()
    {
        var path = Dialogs.Prompt(application, "Install local plugin", "Local plugin directory (contains crewterm-plugin.json)");
        if (string.IsNullOrWhiteSpace(path)) return;
        // daemon 只接受绝对路径：相对路径按工作台进程的当前目录解析。
        var plugin = Call(new("plugin-install", Path: Path.GetFullPath(path.Trim().Trim('"')), Enabled: false)).Plugin!;
        status.Text = "Installed disabled: " + plugin.Name + ". Inspect and enable via F9 → Plugins.";
    }

    /// <summary>
    /// 列出本地插件及其启用状态，选中一个即切换启用/禁用。
    /// </summary>
    /// <exception cref="InvalidOperationException">daemon 版本较旧或调用失败。</exception>
    private void TogglePlugin()
    {
        var plugins = ReadPlugins();
        var index = Dialogs.Choose(application, "LOCAL PLUGINS · select to toggle", plugins.Select(p =>
            $"{(p.Enabled ? "ON" : "OFF")}  {p.Name} {p.Version}  ·  {p.SourceDirectory}").ToArray());
        if (index < 0) return;
        var plugin = Call(new("plugin-enable", PluginId: plugins[index].Id, Enabled: !plugins[index].Enabled)).Plugin!;
        status.Text = (plugin.Enabled ? "Plugin enabled: " : "Plugin disabled: ") + plugin.Name;
    }

    /// <summary>
    /// 显示所选插件的源目录、启用状态与登记时保存的清单（缩进 JSON），并提醒插件命令以当前 Windows 用户身份运行、没有沙箱。
    /// </summary>
    /// <exception cref="InvalidOperationException">daemon 版本较旧或调用失败。</exception>
    private void InspectPlugin()
    {
        var plugins = ReadPlugins();
        var index = Dialogs.Choose(application, "Inspect local plugin", plugins.Select(p => p.Name + "  ·  " + p.SourceDirectory).ToArray());
        if (index < 0) return;
        var plugin = plugins[index];
        Dialogs.Text(application, "PLUGIN MANIFEST · " + plugin.Name,
            $"Source: {plugin.SourceDirectory}\nEnabled: {plugin.Enabled}\nCommands run as your user account, without a sandbox.\n\n" +
            JsonSerializer.Serialize(plugin.Manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 移除所选插件的登记；插件源目录中的文件保持不变。
    /// </summary>
    /// <exception cref="InvalidOperationException">daemon 版本较旧或调用失败。</exception>
    private void RemovePlugin()
    {
        var plugins = ReadPlugins();
        var index = Dialogs.Choose(application, "Remove local plugin registration", plugins.Select(p => p.Name + "  ·  " + p.SourceDirectory).ToArray());
        if (index < 0) return;
        Call(new("plugin-remove", PluginId: plugins[index].Id));
        status.Text = "Registration removed: " + plugins[index].Name + ". Local source files remain.";
    }

    /// <summary>
    /// 在后台通过 daemon 运行一条插件命令，完成后显示其输出。
    /// </summary>
    /// <remarks>
    /// 传给插件的 JSON 数据包含当前项目根目录（<c>projectRoot</c>）、活动面板的会话标识（<c>sessionId</c>）与状态目录（<c>stateDirectory</c>），
    /// 前两者可能为 <see langword="null"/>。daemon 没有返回结果时以消息框报告错误。
    /// </remarks>
    /// <param name="plugin">插件。</param>
    /// <param name="command">要运行的插件命令。</param>
    private void RunPlugin(LocalPluginInfo plugin, LocalPluginCommand command)
    {
        var payload = JsonSerializer.SerializeToElement(new { projectRoot, sessionId = ActiveTerminal.SessionId, stateDirectory });
        RunWork("Running plugin " + plugin.Name + "…", () => client.CallAsync(new("plugin-run", PluginId: plugin.Id,
            PluginCommand: command.Id, Payload: payload), lifetime.Token), response =>
        {
            if (response.PluginResult is not { } result) throw new InvalidOperationException(response.Error ?? "Plugin returned no result");
            ShowPluginResult(result);
        });
    }

    /// <summary>
    /// 列出 daemon 保存的插件执行历史（成功/失败、插件与命令、退出码），选中一条显示完整结果。
    /// </summary>
    private void PluginHistory()
    {
        var results = Call(new("plugin-history")).PluginResults ?? [];
        var choice = Dialogs.Choose(application, "PLUGIN HISTORY", results.Select(r =>
            $"{(r.Success ? "OK" : "FAILED")}  {r.PluginId}/{r.CommandId}  ·  exit {r.ExitCode}").ToArray());
        if (choice >= 0) ShowPluginResult(results[choice]);
    }

    /// <summary>
    /// 在只读查看器中显示一次插件执行的结果：命令、是否成功、退出码、是否超时/取消/截断、错误信息与输出。
    /// </summary>
    /// <param name="result">插件执行结果。</param>
    private void ShowPluginResult(LocalPluginResult result) => Dialogs.Text(application, "PLUGIN OUTPUT · " + result.PluginId,
        $"Command: {result.CommandId}\nSuccess: {result.Success}\nExit: {result.ExitCode}\nTimed out: {result.TimedOut}\n" +
        $"Cancelled: {result.Cancelled}\nTruncated: {result.OutputTruncated}\nError: {result.Error}\n\n{result.Output}");
}
