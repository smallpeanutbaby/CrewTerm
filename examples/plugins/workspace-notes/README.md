# Workspace Notes 本地插件

这是可直接运行的 CrewTerm 本地插件示例，同一份清单在三个平台上都能使用：

- 默认使用 PowerShell 7（`pwsh`）。Linux / macOS 上需要先 [安装 PowerShell](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)。
- 清单中的 `windows` 替代调用让它在 Windows 上改用系统自带的 Windows PowerShell（`powershell.exe`），并以 `-ExecutionPolicy Bypass` 启动，
  因此 Windows 上不需要额外安装，也不受系统默认脚本执行策略影响。

插件系统本身可以运行任何原生可执行程序，不依赖 PowerShell。

`summary` 读取当前项目顶层目录，输出文件数、目录数和最多 20 个名称；扫描最多 1,001 项，超过 1,000 项时返回 `truncated: true`。不读取文件内容。

`record-event` 把 CrewTerm 事件的原始 JSON 写入插件目录的 `events/`，文件名使用时间与随机 ID，便于查看插件实际收到了什么。事件文件由用户保留或删除，示例不会自动清理历史。停用插件即可停止新的事件记录。

在仓库根目录运行（把 `/path/to/project` 换成实际的项目目录）：

```sh
crewterm plugin-install --path ./examples/plugins/workspace-notes
crewterm plugin-enable crewterm.workspace-notes --enabled true
crewterm plugin-run crewterm.workspace-notes --command summary --json '{"workingDirectory":"/path/to/project"}'
crewterm plugin-event --event session.created --json '{"sessionId":"example-session"}'
crewterm plugin-history
```

也可以在界面命令面板选择已启用插件的 `Summarize workspace files` 命令；示例接受 UI 的 `projectRoot` 字段。自动事件包括 `session.created`、`session.exited`、`agent.state` 和 `message.received`。

```sh
crewterm plugin-enable crewterm.workspace-notes --enabled false
crewterm plugin-remove crewterm.workspace-notes
```

移除只撤销注册，保留本目录和事件文件。修改 manifest 后需要移除并重新安装；脚本文件在注册的原目录执行。命令以当前用户权限运行，示例不访问网络、不修改全局执行策略。
