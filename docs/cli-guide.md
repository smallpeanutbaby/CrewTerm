# CrewTerm CLI、后台会话与任务

CrewTerm 当前版本使用 Windows ConPTY，要求 Windows 10 1809 或更高版本。以下示例在 PowerShell 中运行，假定当前目录中已有构建或发布后的 `crewterm.exe`。

## 检查环境

```powershell
.\crewterm.exe --help
.\crewterm.exe --version
.\crewterm.exe doctor
```

`doctor` 显示状态目录及已发现的 Claude Code、Codex、OpenCode、Gemini CLI、GitHub Copilot CLI 和 Shell 可执行文件。它检查 PATH 中的程序位置，包括 Windows `.cmd` 启动脚本；它不会启动 AI 会话、安装工具或验证登录状态。Agent 的安装、登录与计费由对应 CLI 管理。

默认状态目录为 `%LOCALAPPDATA%\CrewTerm`。可以通过 `--state-dir PATH` 使用独立实例；后续命令必须使用相同的目录，才能访问同一批会话和任务。带空格的路径需要加引号。

```powershell
$state = Join-Path $env:LOCALAPPDATA 'CrewTerm'
$project = 'D:\代码\CrewTerm'
.\crewterm.exe --state-dir $state --project $project
```

最后一条命令打开工作区。以下命令可在其他终端中查询和控制同一个后台实例。

## 交互式终端会话

`new` 启动有真实终端的交互式程序，适合 Shell 或 Agent CLI。第一次访问会自动启动 daemon。`new` 成功时返回 JSON，`SessionId` 是后续命令使用的会话 ID。

```powershell
$created = .\crewterm.exe --state-dir $state new `
    --command 'cmd.exe /d /q' --project $project --name 'Shell' | ConvertFrom-Json
$sessionId = $created.SessionId

.\crewterm.exe --state-dir $state send $sessionId --text "echo CREWTERM_READY`r"
.\crewterm.exe --state-dir $state read $sessionId
.\crewterm.exe --state-dir $state sessions
.\crewterm.exe --state-dir $state close $sessionId
```

`send` 按原样发送文本，不自动补回车。上例中的 PowerShell `` `r `` 是实际回车；字面文本 `\r` 不会被 CrewTerm 转成回车。`read` 返回当前终端屏幕的纯文本，而不是 JSON 或完整无限历史；如果子进程还在输出，可以再次读取。

`sessions` 返回 JSON 的 `Sessions` 数组，包含 `Id`、`Name`、`Command`、`WorkingDirectory`、`StartedAt`、`HasExited` 和 `ExitCode`。`close` 结束指定终端及其进程，并移除该会话。

要启动已安装的 Agent，可以把 `--command` 替换为 `claude`、`codex` 或自己的交互式命令。命令在 `--project` 指定目录运行；省略时使用调用 CLI 的当前目录。

## 后台命令任务

`run` 适合构建、测试和其他无需交互的 Shell 命令。任务的标准输入已关闭；需要输入或审批的 Agent 应使用交互式终端会话。`run` 提交后立即返回任务 ID，任务由 daemon 继续执行。

```powershell
$started = .\crewterm.exe --state-dir $state run `
    --command 'dotnet test -c Release' --project $project --name 'Release tests' |
    ConvertFrom-Json
$taskId = $started.SessionId

.\crewterm.exe --state-dir $state tasks
.\crewterm.exe --state-dir $state task $taskId
```

`tasks` 返回 JSON 的 `Tasks` 数组，列出任务摘要，摘要中的 `Output` 为空。`task ID` 返回 JSON 的 `Task` 对象，包含 `Completed`、`ExitCode`、`Cancelled`、`Output` 和 `Error`。输出保留最多 262144 个字符的末尾内容。

在脚本中等待真正的任务结果：

```powershell
do {
    Start-Sleep -Milliseconds 200
    $status = (.\crewterm.exe --state-dir $state task $taskId | ConvertFrom-Json).Task
} while (-not $status.Completed)

$status.Output
if ($status.Cancelled -or $status.ExitCode -ne 0 -or $status.Error) {
    throw "任务未成功：ExitCode=$($status.ExitCode)，Error=$($status.Error)"
}
```

CLI 自身退出码 `0` 表示请求成功处理。它并不表示后台任务已经完成或成功。例如提交 `exit /b 7` 时，`run` 可以返回 `0`，之后 `task ID` 中的 `ExitCode` 为 `7`。CLI 参数错误、未知命令或 daemon 请求失败时，CLI 向 stderr 写入错误并返回 `1`。

取消仍在执行或排队的任务：

```powershell
.\crewterm.exe --state-dir $state cancel $taskId
```

取消会请求终止任务进程树；再次查询 `task ID`，等待 `Completed` 并检查 `Cancelled`。已经完成或不存在的任务无法取消。当前 CLI 没有单独的 pipeline 子命令。

## 离开与停止

关闭普通 CLI 客户端或在工作区按 F10 Detach，会留下 daemon 中的终端和任务。再次使用同一状态目录即可连接。

| 操作 | 影响 |
|---|---|
| `close SESSION_ID` | 结束一个交互式终端会话 |
| `cancel TASK_ID` | 请求取消一个后台任务 |
| `stop` | 停止该状态目录的 daemon、所有终端和后台任务 |

```powershell
.\crewterm.exe --state-dir $state stop
```

daemon 停止时等待任务取消并记录最终结果。重启后可读取已保存的完成任务历史；默认最多保留 100 条。正在运行的进程和终端屏幕不会跨 daemon 重启或系统重启恢复。F10 Detach 不会执行 `stop`。

## 排查问题

- 找不到 Agent：先运行 `doctor` 检查实际程序路径，安装或修正 PATH 后再启动会话。
- 查询不到会话或任务：确认每条命令的 `--state-dir` 相同，并核对使用的是会话 ID 还是任务 ID。
- daemon 无法启动：检查状态目录中的 `server-error.log`；保留损坏配置文件以便定位问题。
- 任务没有成功：查看 `task ID` 的完整结果，不能只看提交命令的退出码或终端中的“完成”字样。

CLI 端到端验证位于 `tests/CrewTerm.App.Tests/CliIntegrationTests.cs`，使用当前构建的 `crewterm.exe`、独立临时状态目录和真实 Shell 命令，不启动已安装的 AI Agent。
