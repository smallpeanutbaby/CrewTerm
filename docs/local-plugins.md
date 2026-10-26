# 本地插件

CrewTerm 从用户选择的本地目录注册 `crewterm-plugin.json`，默认停用。安装不会下载内容，也不会复制或删除源目录。界面可以列出来源、启用、停用、移除、运行命令并查看结果；命令面板显示已启用插件命令。

完整可运行示例见 [Workspace Notes](../examples/plugins/workspace-notes/README.md)。命令行入口：

```powershell
crewterm plugins
crewterm plugin-install --path "D:\plugins\my-plugin"
crewterm plugin-enable my-plugin --enabled true
crewterm plugin-run my-plugin --command inspect --json '{"workingDirectory":"D:\\project"}'
crewterm plugin-event --event session.created --json '{"sessionId":"session-id"}'
crewterm plugin-history
crewterm plugin-enable my-plugin --enabled false
crewterm plugin-remove my-plugin
```

## Manifest 和进程边界

Manifest 使用 `id`、`name`、`version`、`commands`，以及可选 `hooks`。每个 command 需要 `id`、`name` 和 `executable`，可提供 `arguments`、`workingDirectory`、`timeoutSeconds`、`maxOutputCharacters`。hook 使用 `event` 和 `commandId`，必须引用已声明命令。

`executable` 可以是 PATH 中的程序、绝对路径或插件目录内的相对路径。参数通过 `ProcessStartInfo.ArgumentList` 原样传递，不做变量替换或 shell 拼接。需要 shell 的插件必须显式声明 shell 作为 executable。工作目录默认为插件源目录，也可以选择其内部相对目录。

安装保存 manifest 快照；修改命令声明后需要移除并重新安装。源程序仍在原目录执行，因此修改脚本会影响下次运行。启用代表允许以当前用户权限执行声明的本地程序；执行超时和输出限制不构成安全沙箱。停用禁止新的运行，不撤销已经开始的进程。

## JSON 输入和结果

stdin 是一个 UTF-8 JSON 对象，写入后关闭：

```json
{
  "schemaVersion": 1,
  "kind": "command",
  "eventName": null,
  "pluginId": "my-plugin",
  "commandId": "inspect",
  "payload": { "workingDirectory": "D:\\project" }
}
```

事件调用的 `kind` 为 `event`，`eventName` 为事件名。界面命令 payload 提供 `projectRoot`、`sessionId`、`stateDirectory`。自动事件提供 `stateDirectory`、`sessionId`、`detail`；事件有 `session.created`、`session.exited`、`agent.state`、`message.received`。回调不直接获得会话控制权；结果可通过插件历史查看。

返回 `PluginId`、`CommandId`、`Success`、`ExitCode`、`Output`、`Error`、`TimedOut`、`OutputTruncated`、`Cancelled`。`Success` 仅表示未被取消且进程退出码为 0。stdout/stderr 合并保留末尾，截断会明确标记。找不到程序、非零退出、超时、取消和插件错误都转换为结果，不让插件异常终止宿主。

## 上限

| 项目 | 上限或默认值 |
| --- | --- |
| Manifest | 128 KiB |
| 每个插件命令 / hook | 各最多 64 项 |
| 每条命令 arguments | 最多 128 个 |
| stdin JSON envelope | 64 KiB UTF-8 |
| 执行截止时间 | 默认 30 秒，可设 1–300 秒，含等待执行名额 |
| 保留输出 | 默认 65,536 字符，可设 1–1,048,576 字符 |
| Runner 并发 | 默认最多 4 个进程 |

超时或取消会结束已启动的进程树。输入管道提前断开也会清理仍运行的子进程。状态文件损坏不会被悄悄重置为一个空目录。
