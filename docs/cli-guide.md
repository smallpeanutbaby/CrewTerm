# CrewTerm CLI、Agent 协作与后台任务

CrewTerm 支持 Windows 10 1809+、Linux 与 macOS。以下示例使用 PowerShell 语法（Windows PowerShell 5.1，或三个平台上的 PowerShell 7），
假定 `crewterm` 已在 `PATH` 中。在 bash / zsh 中使用时，把 PowerShell 的回车 `` `r `` 写成 `$'\r'`，把 `| ConvertFrom-Json` 换成 `jq` 等工具即可。
功能验收状态见 [0.2 矩阵](verification/v02-features.md)。

## 检查环境

```powershell
crewterm --help
crewterm --version
crewterm doctor
```

`doctor` 显示状态目录及已发现的 Claude Code、Codex、OpenCode、Gemini CLI、GitHub Copilot CLI 和 Shell 可执行文件。它检查 PATH 中的程序位置，包括 Windows 上的 `.cmd` 启动脚本，Linux / macOS 上只认有执行权限的文件；它不会启动 AI 会话、安装工具或验证登录状态。Agent 的安装、登录与计费由对应 CLI 管理。

默认状态目录：Windows 为 `%LOCALAPPDATA%\CrewTerm`，Linux 为 `~/.local/share/CrewTerm`（或 `$XDG_DATA_HOME/CrewTerm`），
macOS 为 `~/Library/Application Support/CrewTerm`。可以通过 `--state-dir PATH` 使用独立实例；后续命令必须使用相同的目录，才能访问同一批会话和任务。带空格的路径需要加引号。

```powershell
$state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CrewTerm'
$project = (Get-Location).Path
crewterm --state-dir $state --project $project
```

最后一条命令打开工作区。以下命令可在其他终端中查询和控制同一个后台实例。

除 `--help`、`-h`、`--version`、`--server` 外，选项采用 `--名称 值`，不要写成无值开关。例如查看全部登记要用 `sessions --all true`，启用插件用 `--enabled true`。会话或插件 ID 可以放在子命令后，也可用 `--id ID`。

## 从旧后台服务切换

新版客户端连接到旧版 daemon 时，会显示版本/协议提示并保留现有终端。新增 Agent、原生恢复、插件等协议功能需要新版 daemon；程序不会自动停止工作、替换运行中的服务或替你升级 provider。

先完成或保存旧实例的工作，再执行：

```powershell
crewterm --state-dir $state stop
# 等待旧服务退出后，再打开新版工作区。
crewterm --state-dir $state --project $project
```

`stop` 会终止这个状态目录下的全部终端与后台任务。想保留旧实例并先试用新版，可给新版指定另一个 `--state-dir`；不同目录互不共享实时会话。

## 交互式终端会话

`new` 启动有真实终端的交互式程序，适合 Shell 或 Agent CLI。第一次访问会自动启动 daemon。`new` 成功时返回 JSON，`SessionId` 是后续命令使用的会话 ID。

```powershell
$created = crewterm --state-dir $state new `
    --command 'cmd.exe /d /q' --project $project --name 'Shell' | ConvertFrom-Json
$sessionId = $created.SessionId

crewterm --state-dir $state send $sessionId --text "echo CREWTERM_READY`r"
crewterm --state-dir $state read $sessionId
crewterm --state-dir $state sessions
crewterm --state-dir $state close $sessionId
```

示例中的 `cmd.exe /d /q` 是 Windows 的交互式 Shell；Linux / macOS 上换成 `/bin/sh`、`bash` 或 `zsh`。

`send` 按原样发送文本，不自动补回车。上例中的 PowerShell `` `r `` 是实际回车；字面文本 `\r` 不会被 CrewTerm 转成回车。`read` 返回当前终端屏幕的纯文本，而不是 JSON 或完整无限历史；如果子进程还在输出，可以再次读取。

`sessions` 返回 daemon 当前持有的终端；其中可能包含已经退出但尚未关闭的进程记录。`sessions --all true` 还包括持久目录中的休眠记录。`Sessions` 元素含 `Id`、`Name`、`Command`、`WorkingDirectory`、`StartedAt`、`HasExited`、`ExitCode`，并追加 `Activity`、`UnreadCount`、`Priority`、`Provider`、`NativeSessionId`、`CanResume`、`ResumeReason`、`IsDormant`。

`close` 结束指定实时终端并保留原生会话登记；删除登记是单独的 `session-forget`。退出状态与任务完成状态不同。

要启动已安装的 Agent，可以把 `--command` 替换为 `claude`、`codex` 或自己的交互式命令。命令在 `--project` 指定目录运行；省略时使用调用 CLI 的当前目录。

`new` 另接受 `--provider NAME`、`--native-id ID`、`--resume-command COMMAND`，用于明确的 provider 或恢复配置。普通 `claude`/`codex` 命令可以从可执行文件名识别 provider；包装脚本需要显式登记，不能假定只更换可执行文件名就支持相同参数。

## 语义状态、通知与等待

```powershell
crewterm --state-dir $state agent-state
crewterm --state-dir $state agent-state $sessionId
crewterm --state-dir $state agent-report $sessionId `
    --state working --source api --reason '已开始构建'
crewterm --state-dir $state agent-report $sessionId `
    --state done --reason '构建退出码为 0，结果已检查'
crewterm --state-dir $state notifications
crewterm --state-dir $state notifications $sessionId
```

有效状态为 `Unknown`、`Working`、`Blocked`、`Done`、`Idle`、`Exited`，输入不区分大小写。单会话结果位于 `Activity`，全部结果位于 `Activities`，包含 `State`、`Source`、`Reason`、`ChangedAt`、`Revision`。`agent-report` 是调用者的显式报告，不会替调用者验证模型或构建是否真的完成。省略 `--source` 时为 `api`。

`Unknown` 表示证据不足；`Blocked` 表示等待输入或决策；`Done` 表示尚未确认的完成；完成通知被显式确认后可变为 `Idle`。进入 Done/Blocked 会创建通知，重复观察同一状态不会重复创建。通知读取不会修改已读状态：

```powershell
$notices = (crewterm --state-dir $state notifications $sessionId | ConvertFrom-Json).Notifications
# 查看后，明确选择一个通知进行确认。
crewterm --state-dir $state notification-read $notices[0].Id
```

Blocked 的通知被标为已读后仍然是 Blocked，不等于用户已批准工具。优先级依次为等待输入、未读完成、未确认消息、工作中。界面通过 F9 的 `Agent: Notifications`、`Agent: Mark selected as read` 和 `Agent: Explain selected status` 操作；仅轮询或切换列表不会自动确认。

```powershell
$before = (crewterm --state-dir $state agent-state $sessionId | ConvertFrom-Json).Activity
crewterm --state-dir $state agent-wait $sessionId `
    --state 'done,blocked,exited' --timeout-ms 30000 --after-revision $before.Revision
```

`agent-wait` 等待任一目标状态，并可要求 `Revision` 严格大于 `--after-revision`。省略 revision 时，当前状态已匹配便立即返回。默认等待 10,000 毫秒；允许大于 0、最多 60,000 毫秒；同时最多 8 个等待。超时属于失败，CLI 返回 1。界面入口是 `Agent: Wait for selected state`，选择一个状态及 1–60 秒超时，后台等待期间可继续使用界面。

## 收件箱、提示与任务交接

`message-send` 只保存定向消息，不在收件人的终端里输入文字；`agent-prompt` 直接发送文字并补一个回车；`agent-handoff` 保存消息后尝试把同样内容作为提示提交。后两者需要明确的存活且未 Blocked 目标，不能代替处理审批。

```powershell
$from = 'SENDING_CREWTERM_ID'
$to = 'RECEIVING_CREWTERM_ID'
$sent = crewterm --state-dir $state message-send `
    --from $from --to $to --text '请检查 parser 改动' | ConvertFrom-Json

crewterm --state-dir $state message-list $to
crewterm --state-dir $state message-ack $to --message $sent.Message.Id
crewterm --state-dir $state message-send `
    --from $to --to $from --text '已检查，建议见说明' --reply-to $sent.Message.Id

crewterm --state-dir $state agent-prompt $to --text '请执行已确认的测试任务'
crewterm --state-dir $state agent-handoff `
    --from $from --to $to --text '请接手修改并报告结果'
```

两个 ID 都是 CrewTerm 会话 ID，不是 provider UUID。消息返回 `Message`，包含 `Id`、`FromSessionId`、`ToSessionId`、`Text`、`CreatedAt`、`Acknowledged`、`InReplyTo`。`--reply-to` 必须引用对方发给当前发送者的消息；只有收件方可以确认该消息。列表不会自动确认。界面入口为 `Agent: Send message`、`Agent: Inbox`、`Agent: Handoff prompt`；打开具体收件箱消息后才确认它。

检查 `Delivered` 区分“已入箱”和“已向终端提交”。即使投递成功，也只说明输入已发送，不证明模型已经执行、理解或完成任务。若保存消息成功但终端投递失败，错误会明确说明，消息仍可在收件箱检查，避免盲目重复提交。消息和提示最多 32,768 字符；提示拒绝 NUL 与终端 escape 控制序列。通知最多保存 200 条，消息最多 500 条。

## 原生会话恢复

```powershell
crewterm --state-dir $state sessions --all true
crewterm --state-dir $state session-bind $sessionId `
    --provider codex --native-id 'EXACT_NATIVE_UUID'
crewterm --state-dir $state session-resume $sessionId
crewterm --state-dir $state session-restart $sessionId
crewterm --state-dir $state session-forget $sessionId
```

这些是不同操作：`session-resume` 续接登记的精确对话；`session-restart` 明确开始新对话；`session-forget` 移除已经关闭的记录及相关 CrewTerm 活动资料，保留 provider 对话文件。正在运行的会话不会重复 resume，遗忘前需要先 close。

Claude、Codex、Gemini、Copilot 的自动恢复适配要求完整 UUID，OpenCode 要求 `ses_...` ID。不会拿名称、前缀或 `--last` 猜测目标。未知 provider 或复杂命令可登记完整命令：

```powershell
crewterm --state-dir $state session-bind $sessionId `
    --provider custom --resume-command 'my-agent resume exact-conversation-id'
```

已验证的 model/profile/settings/cwd 参数会保留，初始用户 prompt 和旧 selector 不会重发；未知选项、可变参数数量、复合 shell 命令和 wrapper 需要显式恢复命令。界面提供 Resume、Restart、Bind、Forget 四个独立入口。详细边界见 [原生会话说明](native-sessions.md)。

普通 Claude 启动预分配 UUID，受支持启动可用调用级 `--settings` 接入身份和状态 hook。已有 `--settings`、`--bare`、`--safe-mode` 时保留原配置并跳过自动接入。Codex 使用调用级 `notify`，只在 turn 完成时接收 `thread-id`；已有 notify 配置时保留原设置并说明未自动连接。Codex 的 Working 可来自输入已提交的推断，不能冒充 provider 确认。回调不会输出权限决策，不会绕过 hook trust，也不会自行启动 daemon。

`agent-hook` 是由集成生成的被动回调入口：Claude 使用 stdin JSON，Codex 把 JSON 放在最后一个 argv。普通脚本上报使用 `agent-report`。真实 fixture→程序→管道和精确参数恢复有自动测试；尚未通过付费模型回合实测 provider 历史回读，最终回调集成证据见 [矩阵](verification/v02-features.md)。

## 本地插件

```powershell
crewterm --state-dir $state plugins
crewterm --state-dir $state plugin-install `
    --path 'D:\代码\CrewTerm\examples\plugins\workspace-notes'
crewterm --state-dir $state plugin-enable crewterm.workspace-notes --enabled true
$payload = @{ workingDirectory = $project } | ConvertTo-Json -Compress
crewterm --state-dir $state plugin-run crewterm.workspace-notes --command summary --json $payload
crewterm --state-dir $state plugin-history
crewterm --state-dir $state plugin-enable crewterm.workspace-notes --enabled false
crewterm --state-dir $state plugin-remove crewterm.workspace-notes
```

> Windows PowerShell 5.1 与 PowerShell 7.3 之前的版本向原生程序传参时不会转义参数中的双引号，`crewterm` 收到的 JSON 会缺少引号，
> 报 `--json must be valid JSON`。在这些版本中请写成 `--json ($payload -replace '"', '\"')`；PowerShell 7.3 及以上版本、bash / zsh 不需要。

安装默认停用，也可明确指定 `plugin-install --enabled true`。`plugin-enable ID --enabled false` 用于停用，没有单独的 `plugin-disable` 命令。移除只撤销注册，不删除本地程序、脚本或插件产生的文件。已安装启用的命令出现在界面的 F9 面板；`Plugins: Inspect local manifest` 可以检查实际来源与声明。

```powershell
crewterm --state-dir $state plugin-event --event session.created `
    --json '{"sessionId":"example-session"}'
```

自动事件为 `session.created`、`session.exited`、`agent.state`、`message.received`。运行和事件通过 JSON stdin 传递，实际命令受 manifest 限定；默认超时 30 秒，可设 1–300 秒；输出默认保留末尾 65,536 个字符，最多 1,048,576 个字符。进程失败、超时与取消会变成可查看结果，不应让 daemon 崩溃。插件以当前用户权限执行，不是安全沙箱。插件市场和远程安装不在本版范围。

成功返回 `Plugin`、`Plugins`、`PluginResult` 或 `PluginResults`。失败时 CLI 写 stderr、返回 1，可用 `plugin-history` 查看最近的执行结果（保存在 daemon 内存中，daemon 停止后清空）；不要只检查是否有输出。详细 manifest、输入格式、上限和实际示例见 [本地插件说明](local-plugins.md)。

## 后台命令任务

`run` 适合构建、测试和其他无需交互的 Shell 命令。任务的标准输入已关闭；需要输入或审批的 Agent 应使用交互式终端会话。`run` 提交后立即返回任务 ID，任务由 daemon 继续执行。

```powershell
$started = crewterm --state-dir $state run `
    --command 'dotnet test -c Release' --project $project --name 'Release tests' |
    ConvertFrom-Json
$taskId = $started.SessionId

crewterm --state-dir $state tasks
crewterm --state-dir $state task $taskId
```

`tasks` 返回 JSON 的 `Tasks` 数组，列出任务摘要，摘要中的 `Output` 为空。`task ID` 返回 JSON 的 `Task` 对象，包含 `Completed`、`ExitCode`、`Cancelled`、`Output` 和 `Error`。输出保留最多 262144 个字符的末尾内容。

在脚本中等待真正的任务结果：

```powershell
do {
    Start-Sleep -Milliseconds 200
    $status = (crewterm --state-dir $state task $taskId | ConvertFrom-Json).Task
} while (-not $status.Completed)

$status.Output
if ($status.Cancelled -or $status.ExitCode -ne 0 -or $status.Error) {
    throw "任务未成功：ExitCode=$($status.ExitCode)，Error=$($status.Error)"
}
```

CLI 自身退出码 `0` 表示请求成功处理。它并不表示后台任务已经完成或成功。例如提交 `exit /b 7` 时，`run` 可以返回 `0`，之后 `task ID` 中的 `ExitCode` 为 `7`。CLI 参数错误、未知命令、等待超时、插件失败或 daemon 请求失败时，CLI 向 stderr 写入错误并返回 `1`。

取消仍在执行或排队的任务：

```powershell
crewterm --state-dir $state cancel $taskId
```

取消会请求终止任务进程树；再次查询 `task ID`，等待 `Completed` 并检查 `Cancelled`。已经完成或不存在的任务无法取消。当前 CLI 没有单独的 pipeline 子命令。

## 离开与停止

关闭普通 CLI 客户端或在工作区按 F10 Detach，会留下 daemon 中的终端和任务。再次使用同一状态目录即可连接。

| 操作 | 影响 |
|---|---|
| 关闭 tab / pane | 只移除布局；相关进程继续运行 |
| `close SESSION_ID` | 结束一个交互式终端，保留原生会话登记 |
| `session-forget SESSION_ID` | 遗忘已关闭的 CrewTerm 登记，保留 provider 历史 |
| `cancel TASK_ID` | 请求取消一个后台任务 |
| `stop` | 停止该状态目录的 daemon、所有终端和后台任务；daemon 未运行时只提示 “not running”，不会先启动一个再停止 |

```powershell
crewterm --state-dir $state stop
```

daemon 停止时等待任务取消并记录最终结果。重启后可读取已保存的完成任务历史；默认最多保留 100 条。正在运行的进程和终端屏幕不会跨 daemon 重启或系统重启恢复；持久目录中的原生对话可以按已登记身份显式续接。F10 Detach 不会执行 `stop`。

## 排查问题

- 找不到 Agent：先运行 `doctor` 检查实际程序路径，安装或修正 PATH 后再启动会话。
- 项目里的脚本无法作为终端命令启动：命令开头的裸程序名（如 `build.cmd`、`build.sh`）只在 PATH 的绝对路径条目中查找，不会查找项目目录，防止打开不受信任的仓库时执行其中同名的程序；运行项目内脚本请写成 `.\build.cmd`、`./build.sh` 或绝对路径。Linux / macOS 上脚本还需要执行权限（`chmod +x build.sh`）。
- Windows 会话内的 cmd 找不到项目里的脚本：CrewTerm 启动的终端设置了 `NoDefaultCurrentDirectoryInExePath=1`，cmd.exe 不再从当前目录查找程序（与 PowerShell 的行为一致），以免 Agent 的 npm 启动脚本执行仓库中植入的 `node.bat`。在终端里请输入 `.\build.cmd`。
- Linux / macOS 上原生恢复显示需要登记恢复命令：启动命令含 `$`、反引号、反斜杠、引号外的单引号或 `;` 等 Shell 语法时，CrewTerm 无法可靠改写它，请用 `session-bind --resume-command` 登记完整的恢复命令。
- 相对路径：`--project`、`--path` 的相对路径按执行 CLI 时的当前目录解析；daemon 只接受绝对路径。
- 命令缺少目标：`send`、`read`、`close`、`task`、`cancel` 等需要会话或任务 ID 的命令缺少 ID 时直接报参数错误，不会连接或启动 daemon。
- 查询不到会话或任务：确认每条命令的 `--state-dir` 相同，并核对使用的是会话 ID 还是任务 ID。
- daemon 无法启动：检查状态目录中的 `server-error.log`；保留损坏配置文件以便定位问题。
- 任务没有成功：查看 `task ID` 的完整结果，不能只看提交命令的退出码或终端中的“完成”字样。
- 新命令提示旧协议：先完成旧 daemon 的工作，明确 stop 后再启动新版；不要把换了客户端当成后台已升级。
- 原生恢复不可用：查看 `CanResume` 和 `ResumeReason`，确认精确 ID 是否已捕获、原命令是否存在歧义；必要时绑定完整恢复命令。
- Agent 状态不明确：查看 `Activity.Source` 和 `Reason`。provider 自动 hook 可能因已有配置被保留而未连接；普通输出不能证明完成。
- 插件没运行：检查启用状态、manifest 来源、`plugin-history` 中的错误及实际解释器是否在 PATH；示例插件默认使用 PowerShell 7（`pwsh`，Linux / macOS 上需要安装），Windows 上通过 `windows` 替代调用改用系统自带的 Windows PowerShell，无需另装。

基础 CLI 与 Agent/native/plugin 集成测试使用构建后的 `crewterm`、独立临时状态目录、真实 Shell 与伪终端和可控 provider fixture，并在 Windows、Linux 与 macOS 上运行；不会通过付费 AI 提示验证模型历史。来源及最终发布验收见 [功能矩阵](verification/v02-features.md)。
