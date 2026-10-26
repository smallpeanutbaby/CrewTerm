# 多 Agent 工作流实战

本文是给已经能在 CrewTerm 中启动 Agent 的用户准备的配方集：每个配方给出目标、界面操作与 CLI 两种步骤，以及需要注意的边界。
状态、消息、交接与等待的确切语义见 [Agent 与 Provider 集成](agent-integration.md)，全部命令与参数见 [CLI 使用说明](cli-guide.md)。

## 目录

- [开始之前](#开始之前)
- [实现者与审查者](#实现者与审查者)
- [用脚本编排两个 Agent](#用脚本编排两个-agent)
- [后台任务失败时交给 Agent](#后台任务失败时交给-agent)
- [分离、重连与重启后续接](#分离重连与重启后续接)
- [用插件在完成或等待输入时提醒](#用插件在完成或等待输入时提醒)
- [并行子任务的隔离与汇总](#并行子任务的隔离与汇总)

## 开始之前

- **分工由你决定**：CrewTerm 不会自动拆分任务、自动合并修改、隐式广播，也不会替 Agent 审批。下面每一步的收件人、内容和等待条件都是明确写出的。
- **ID**：`new` 返回的 `SessionId` 是 12 位十六进制的 CrewTerm 会话 ID，`run` 返回的 `SessionId` 是任务 ID；消息与通知另有自己的 `Id`。
- **CLI 输出**：除 `read` 输出屏幕文字外，子命令都输出缩进的 JSON，字段名为 `Ok`、`SessionId`、`Activity`、`Message` 这样的大写开头形式；
  成功返回 0，失败在标准错误写 `CrewTerm: <原因>` 并返回 1。
- **文字限制**：选项的值不能以 `--` 开头；提示与消息最多 32,768 个字符，除换行、回车、制表符外不能含控制字符。
  多行提示要求目标已启用 bracketed paste，而 CLI 无法查询这一点，所以脚本中的提示都写成一行。
- **`read` 只返回当前屏幕**，不是完整历史；CLI 创建的会话在界面中显示之前是 100 列 × 30 行。需要完整结论时，让 Agent 把结论写进文件。
- **首次运行的确认界面**：Agent CLI 第一次在某个目录运行时可能显示登录、信任目录等界面。CrewTerm 只识别三种审批提示，
  不认识这些界面，脚本投递的文字可能被当成对它们的回答；请先在界面中手动运行一次并完成确认。
- **脚本约定**：下面的脚本在环境变量 `CREWTERM_STATE_DIR` 非空时给每条命令加上 `--state-dir "$CREWTERM_STATE_DIR"`。
  在 CrewTerm 终端里运行脚本时，这个变量已经指向所属后台服务的状态目录；`crewterm` 的普通子命令本身并不读取它。
- **依赖**：bash 版本需要 `jq`，面向 Linux 与 macOS（bash 3.2 及以上）；PowerShell 版本适用于 Windows PowerShell 5.1 与 PowerShell 7。
  Windows PowerShell 5.1 中，含中文的脚本要保存为带 BOM 的 UTF-8，传给原生程序的参数里不要包含双引号（5.1 不会转义它们）。

## 实现者与审查者

**目标**：一个 Agent 在独立的 Git worktree 中实现功能，另一个 Agent 在自己的 worktree 中审查；审查任务通过交接或收件箱传递，
两者互不改动对方的文件。

### 界面操作

1. 打开主仓库：`crewterm --project ~/work/app`。
2. F6 → `Worktrees: create isolated branch`：分支名填 `crew/impl`，目录填 `~/work/app-impl`（默认是项目目录加 `-worktree`）。
   创建后 CrewTerm 把它加入项目列表并切换过去。
3. F3 → 选择 Claude Code：Agent 在当前项目，也就是这个 worktree 的根目录中启动。
4. 在项目列表中回到主仓库，重复第 2 步创建 `crew/review` 与 `~/work/app-review`，再用 F3 启动审查者，例如 Codex。
5. F9 → `Terminal: Split right`，在两个面板中分别用 `Terminal: Choose session for active pane` 选择实现者与审查者。
6. 在实现者的终端里输入任务。它完成时侧边栏显示 `[DONE*]`，状态栏给出提示；让它把改动提交到 `crew/impl`，
   或者你在它的终端里自己 `git commit`（F6 只提供查看、diff 与单文件暂存，不提供提交）。
7. 聚焦实现者的面板，F9 → `Agent: Handoff prompt` → 选择审查者 → 输入一行文字，例如
   `请审查 git diff main...crew/impl，只给出意见，不要修改文件`。这会在审查者的收件箱里留下一条来自实现者的消息，
   并把同样的文字提交到审查者的终端。
8. 只想留言、不想直接提交时，改用 `Agent: Send message`；之后聚焦审查者的面板，`Agent: Inbox` 打开这条消息（关闭详情时确认），
   再由你决定在审查者终端里输入什么。
9. 审查结束后，聚焦审查者的面板，用 `Agent: Send message` 把结论发回实现者。界面不能设置回复关联，需要 `--reply-to` 时用下面的 CLI。
10. 合并与清理由你完成：在主仓库中 `git merge crew/impl`；F6 → `Worktrees: remove clean checkout` 删除不再需要的 worktree
    （有未提交修改时 Git 会拒绝，分支保留）。

### CLI

CrewTerm 的 CLI 没有 worktree 子命令，直接使用 `git`。

```sh
git -C ~/work/app worktree add -b crew/impl ../app-impl HEAD
git -C ~/work/app worktree add -b crew/review ../app-review HEAD

IMPL=$(crewterm new --command claude --project ~/work/app-impl --name Implementer | jq -r .SessionId)
REVIEW=$(crewterm new --command codex --project ~/work/app-review --name Reviewer | jq -r .SessionId)

crewterm agent-prompt "$IMPL" --text "实现 CSV 导出，完成后把改动提交到当前分支 crew/impl"
# …等实现者进入 Done（见下一节的 wait_turn）…

MSG=$(crewterm agent-handoff --from "$IMPL" --to "$REVIEW" \
  --text "请审查 git diff main...crew/impl，只给出意见，不要修改文件" | jq -r .Message.Id)
crewterm message-ack "$REVIEW" --message "$MSG"      # 审查者已经收到，确认这条消息

# 审查者的结论作为回复发回实现者的收件箱。
crewterm message-send --from "$REVIEW" --to "$IMPL" --reply-to "$MSG" --text "审查意见：导出缺少表头转义"
crewterm message-list "$IMPL" | jq -r '.Messages[] | select(.Acknowledged | not) | "\(.Id)  \(.Text)"'
```

```powershell
git -C "$HOME/work/app" worktree add -b crew/impl ../app-impl HEAD
git -C "$HOME/work/app" worktree add -b crew/review ../app-review HEAD

$impl = (crewterm new --command claude --project "$HOME/work/app-impl" --name Implementer | ConvertFrom-Json).SessionId
$review = (crewterm new --command codex --project "$HOME/work/app-review" --name Reviewer | ConvertFrom-Json).SessionId

crewterm agent-prompt $impl --text '实现 CSV 导出，完成后把改动提交到当前分支 crew/impl'
# …等实现者进入 Done（见下一节的 Wait-Turn）…

$msg = (crewterm agent-handoff --from $impl --to $review `
    --text '请审查 git diff main...crew/impl，只给出意见，不要修改文件' | ConvertFrom-Json).Message.Id
crewterm message-ack $review --message $msg

crewterm message-send --from $review --to $impl --reply-to $msg --text '审查意见：导出缺少表头转义'
(crewterm message-list $impl | ConvertFrom-Json).Messages | Where-Object { -not $_.Acknowledged } | Format-Table Id, Text
```

### 注意事项

- 同一目录里的多个 Agent 会看到彼此的修改。worktree 让它们各有工作目录，但共享同一个仓库的分支与提交；
  同一个分支不能同时在两个 worktree 中检出。
- 审查者的 worktree 只能通过 Git 看到已经提交到 `crew/impl` 的内容；未提交的改动只存在于实现者的目录里。
- “只给出意见、不要修改文件”只是写给模型的要求，CrewTerm 不限制 Agent 能改哪些文件。
- 交接要求审查者正在运行且不是 `Blocked`；`Delivered: true` 只说明文字已写入它的终端，不说明它已经开始审查。
- 收件箱是给你和脚本看的。Agent 想读自己的收件箱，需要在它的终端里运行
  `"$CREWTERM_EXE" --state-dir "$CREWTERM_STATE_DIR" message-list "$CREWTERM_SESSION_ID"`。
- 示例中的 `main` 换成你的主分支名。

## 用脚本编排两个 Agent

**目标**：一个脚本完成“启动两个 Agent → 给实现者投递任务 → 等它 `Done` 或 `Blocked` → 读取屏幕 → 交接给审查者 → 等审查结束”。
两个 Agent 都在同一个项目目录中运行；需要隔离时先按上一节为它们各建 worktree。

脚本流程：

1. `new` 启动两个会话，记下 `SessionId`。
2. 反复 `read` 屏幕，直到内容非空且两次读取相同，作为“CLI 已就绪”的判断。这是脚本自己的启发式方法，CrewTerm 不提供就绪信号
   （接入了 hook 的 Claude Code 会在启动时进入 `Idle`，可以改用 `agent-wait --state idle`）。
3. 记下实现者当前的 `Revision`，用 `agent-prompt` 投递任务。
4. 循环调用 `agent-wait --state done,blocked,exited --after-revision <Revision> --timeout-ms 60000`：单次等待最多 60 秒，
   超时（标准错误含 `Timed out waiting for Agent state`）就继续等；遇到 `Blocked` 时提醒你到界面中处理，然后从这条观测的 `Revision` 继续等。
5. 用 `read` 把实现者的屏幕保存到 `.crewterm-run/implementer.txt`，再用 `agent-handoff` 把审查任务交给审查者，并同样等待它结束。

### bash

```bash
#!/usr/bin/env bash
# crew-review.sh：启动实现者与审查者两个 Agent，实现者完成后把审查任务交接给审查者。
# 依赖：bash 3.2+、jq，crewterm 在 PATH 中。
# 用法：./crew-review.sh <项目目录> "<任务描述>"
set -euo pipefail

PROJECT=$(cd "${1:?用法: crew-review.sh <项目目录> <任务描述>}" && pwd)
TASK=${2:?缺少任务描述}
IMPL_CMD=${IMPL_CMD:-claude}          # 实现者的启动命令
REVIEW_CMD=${REVIEW_CMD:-codex}       # 审查者的启动命令
TURN_SECONDS=${TURN_SECONDS:-1800}    # 每一轮最多等待的秒数
OUT_DIR="$PROJECT/.crewterm-run"

# 调用 crewterm；CREWTERM_STATE_DIR 非空时使用该状态目录（在 CrewTerm 终端中会自动设置）。
ct() {
  if [ -n "${CREWTERM_STATE_DIR:-}" ]; then
    crewterm --state-dir "$CREWTERM_STATE_DIR" "$@"
  else
    crewterm "$@"
  fi
}

# 脚本自己的就绪判断：屏幕非空，且间隔 2 秒的两次读取完全相同。
wait_ready() {
  local id=$1 previous="" current i
  for i in $(seq 1 30); do
    current=$(ct read "$id")
    if [ -n "$(printf '%s' "$current" | tr -d '[:space:]')" ] && [ "$current" = "$previous" ]; then
      return 0
    fi
    previous=$current
    sleep 2
  done
  echo "会话 $id 在 60 秒内没有稳定下来" >&2
  return 1
}

# 输出会话当前观测的 Revision。
revision() { ct agent-state "$1" | jq -r '.Activity.Revision'; }

# 等待会话在给定 Revision 之后进入 Done、Blocked 或 Exited，输出“状态 Revision”。
# 单次 agent-wait 最多 60 秒，超时后继续等待；总时长超过 TURN_SECONDS 时输出“Timeout 原 Revision”。
wait_turn() {
  local id=$1 after=$2 deadline=$((SECONDS + TURN_SECONDS)) out err
  err=$(mktemp)
  while [ "$SECONDS" -lt "$deadline" ]; do
    if out=$(ct agent-wait "$id" --state done,blocked,exited --timeout-ms 60000 --after-revision "$after" 2>"$err"); then
      rm -f "$err"
      printf '%s\n' "$out" | jq -r '.Activity | "  \(.State) · \(.Source) · \(.Reason)"' >&2
      printf '%s\n' "$out" | jq -r '.Activity | "\(.State) \(.Revision)"'
      return 0
    fi
    if ! grep -q 'Timed out waiting for Agent state' "$err"; then
      cat "$err" >&2
      rm -f "$err"
      return 1
    fi
  done
  rm -f "$err"
  echo "Timeout $after"
}

# 等待一轮结束：Blocked 时提醒你到界面中处理，然后继续等待下一次变化；输出最终状态。
finish_turn() {
  local id=$1 after=$2 result state
  while :; do
    result=$(wait_turn "$id" "$after") || return 1
    state=${result%% *}
    after=${result##* }
    if [ "$state" != Blocked ]; then
      echo "$state"
      return 0
    fi
    echo "会话 $id 正在等待输入：请在界面中查看并处理（F9 → Agent: Notifications）" >&2
  done
}

mkdir -p "$OUT_DIR"
IMPL=$(ct new --command "$IMPL_CMD" --project "$PROJECT" --name Implementer | jq -r .SessionId)
REVIEW=$(ct new --command "$REVIEW_CMD" --project "$PROJECT" --name Reviewer | jq -r .SessionId)
echo "实现者 $IMPL，审查者 $REVIEW" >&2
wait_ready "$IMPL"
wait_ready "$REVIEW"

before=$(revision "$IMPL")
ct agent-prompt "$IMPL" --text "$TASK" > /dev/null
state=$(finish_turn "$IMPL" "$before")
ct read "$IMPL" > "$OUT_DIR/implementer.txt"
if [ "$state" != Done ]; then
  echo "实现者没有完成（$state），屏幕已保存到 $OUT_DIR/implementer.txt" >&2
  exit 1
fi

before=$(revision "$REVIEW")
ct agent-handoff --from "$IMPL" --to "$REVIEW" \
  --text "请审查 $PROJECT 中尚未提交的改动（git diff），实现者最后的屏幕保存在 $OUT_DIR/implementer.txt。只给出审查意见，不要修改文件。" |
  jq -r '"交接消息 \(.Message.Id)，Delivered=\(.Delivered)"' >&2
state=$(finish_turn "$REVIEW" "$before")
ct read "$REVIEW" > "$OUT_DIR/reviewer.txt"
echo "审查者结束状态：$state；屏幕已保存到 $OUT_DIR/reviewer.txt" >&2
```

运行：

```sh
chmod +x crew-review.sh
./crew-review.sh ~/work/app "为 src/export 增加 CSV 导出，并补上单元测试"
IMPL_CMD="claude --model sonnet" REVIEW_CMD=claude ./crew-review.sh ~/work/app "修复登录超时"
```

### PowerShell

```powershell
# crew-review.ps1：启动实现者与审查者两个 Agent，实现者完成后把审查任务交接给审查者。
# 适用于 Windows PowerShell 5.1 与 PowerShell 7；crewterm 在 PATH 中。
# 用法：./crew-review.ps1 -Project <项目目录> -Task '<任务描述>'
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Task,
    [string]$ImplementerCommand = 'claude',
    [string]$ReviewerCommand = 'codex',
    [int]$TurnSeconds = 1800
)
$ErrorActionPreference = 'Stop'
# read 输出的是 UTF-8 屏幕文字。
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$Project = (Resolve-Path -LiteralPath $Project).Path
$outDir = Join-Path $Project '.crewterm-run'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 运行 crewterm，返回退出码、标准输出与标准错误；非零退出码不会抛出异常。
function Invoke-Crew([string[]]$Arguments) {
    if ($env:CREWTERM_STATE_DIR) { $Arguments = @('--state-dir', $env:CREWTERM_STATE_DIR) + $Arguments }
    $ErrorActionPreference = 'Continue'
    $lines = @(& crewterm @Arguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($lines | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join "`n"
        Error    = ($lines | Where-Object { $_ -is [Management.Automation.ErrorRecord] }) -join "`n"
    }
}

# 运行 crewterm 并把标准输出解析为 JSON；失败时抛出异常。
function Get-CrewJson([string[]]$Arguments) {
    $result = Invoke-Crew $Arguments
    if ($result.ExitCode -ne 0) { throw "crewterm $($Arguments -join ' ') 失败：$($result.Error)" }
    $result.Output | ConvertFrom-Json
}

# 脚本自己的就绪判断：屏幕非空，且间隔 2 秒的两次读取完全相同。
function Wait-Ready([string]$Id) {
    $previous = $null
    for ($i = 0; $i -lt 30; $i++) {
        $current = (Invoke-Crew @('read', $Id)).Output
        if ($current.Trim() -and $current -ceq $previous) { return }
        $previous = $current
        Start-Sleep -Seconds 2
    }
    throw "会话 $Id 在 60 秒内没有稳定下来"
}

# 等待会话在给定 Revision 之后进入 Done、Blocked 或 Exited；单次 agent-wait 最多 60 秒，超时后继续等待。
# 总时长超过 TurnSeconds 时返回 $null。
function Wait-Turn([string]$Id, [long]$After) {
    $deadline = (Get-Date).AddSeconds($TurnSeconds)
    while ((Get-Date) -lt $deadline) {
        $result = Invoke-Crew @('agent-wait', $Id, '--state', 'done,blocked,exited',
            '--timeout-ms', '60000', '--after-revision', "$After")
        if ($result.ExitCode -eq 0) {
            $activity = ($result.Output | ConvertFrom-Json).Activity
            Write-Host "  $($activity.State) · $($activity.Source) · $($activity.Reason)"
            return $activity
        }
        if ($result.Error -notmatch 'Timed out waiting for Agent state') { throw $result.Error }
    }
    return $null
}

# 等待一轮结束：Blocked 时提醒你到界面中处理，然后继续等待下一次变化。
function Complete-Turn([string]$Id, [long]$After) {
    while ($true) {
        $activity = Wait-Turn $Id $After
        if ($null -eq $activity -or $activity.State -ne 'Blocked') { return $activity }
        Write-Warning "会话 $Id 正在等待输入：请在界面中查看并处理（F9 → Agent: Notifications）"
        $After = $activity.Revision
    }
}

$impl = (Get-CrewJson @('new', '--command', $ImplementerCommand, '--project', $Project, '--name', 'Implementer')).SessionId
$review = (Get-CrewJson @('new', '--command', $ReviewerCommand, '--project', $Project, '--name', 'Reviewer')).SessionId
Write-Host "实现者 $impl，审查者 $review"
Wait-Ready $impl
Wait-Ready $review

$before = (Get-CrewJson @('agent-state', $impl)).Activity.Revision
Get-CrewJson @('agent-prompt', $impl, '--text', $Task) | Out-Null
$activity = Complete-Turn $impl $before
$implScreen = Join-Path $outDir 'implementer.txt'
(Invoke-Crew @('read', $impl)).Output | Set-Content -LiteralPath $implScreen -Encoding UTF8
if ($null -eq $activity -or $activity.State -ne 'Done') { throw "实现者没有完成，屏幕已保存到 $implScreen" }

$before = (Get-CrewJson @('agent-state', $review)).Activity.Revision
$text = "请审查 $Project 中尚未提交的改动（git diff），实现者最后的屏幕保存在 $implScreen。只给出审查意见，不要修改文件。"
$handoff = Get-CrewJson @('agent-handoff', '--from', $impl, '--to', $review, '--text', $text)
Write-Host "交接消息 $($handoff.Message.Id)，Delivered=$($handoff.Delivered)"
$activity = Complete-Turn $review $before
$reviewScreen = Join-Path $outDir 'reviewer.txt'
(Invoke-Crew @('read', $review)).Output | Set-Content -LiteralPath $reviewScreen -Encoding UTF8
Write-Host "审查者结束状态：$(if ($activity) { $activity.State } else { 'Timeout' })；屏幕已保存到 $reviewScreen"
```

运行：

```powershell
./crew-review.ps1 -Project "$HOME/work/app" -Task '为 src/export 增加 CSV 导出，并补上单元测试'
./crew-review.ps1 -Project D:\work\app -Task '修复登录超时' -ReviewerCommand claude
```

### 注意事项

- 脚本创建的会话也会出现在界面中：打开 `crewterm` 后用 `Terminal: Choose session for active pane` 观看；在面板中显示后屏幕尺寸改为面板尺寸。
- `Done` 只会来自 Claude Code 的 `Stop` hook、Codex 的 notify 或 `agent-report`。用没有 hook 的 Agent，或 hook 因已有配置而跳过时
  （启动后 `agent-state` 的 Reason 会说明），脚本会一直等到 `TURN_SECONDS`；这时让该 Agent 自行上报，
  见 [用 agent-report 自行上报](agent-integration.md#用-agent-report-自行上报)。
- `agent-prompt` 不等待 Agent 空闲，也不确认模型已经开始执行；`Delivered` 只表示文字已写入终端。
- 任务描述只能是一行，不能以 `--` 开头。审查者处于 `Blocked` 时交接会失败，脚本随之退出。
- 每轮等待各占用一个 `agent-wait` 名额；每个后台服务同时最多 8 个，同时运行很多这样的脚本时会有等待立即失败。
- `.crewterm-run/` 建议加入 `.gitignore` 或 `.git/info/exclude`。

## 后台任务失败时交给 Agent

**目标**：在后台服务中运行测试或流水线；失败时把输出保存到文件，并让指定的 Agent 去修复。

### 界面操作

1. F7 → `Run command`，输入测试命令（默认 `dotnet test`），任务在当前项目根目录中运行并打开输出窗口；
   `Close (keep running)` 关闭窗口而任务继续，`Cancel task` 取消任务。
2. 多个步骤：F7 → `Create pipeline template` 保存步骤，`Run pipeline` 运行；步骤按顺序执行，任一步失败即停止。
3. 稍后用 F7 → `Live tasks / history / cancel` 查看结果。
4. CrewTerm 没有把任务输出直接交给 Agent 的界面入口。可以在 Agent 的终端里告诉它失败的命令并让它自己重跑，
   或者用下面的脚本把输出写进文件后交给它。

### CLI

CLI 没有流水线子命令。`run` 的命令经 Shell 执行（Windows 为 `cmd.exe /d /s /c`，Linux / macOS 为 `/bin/sh -c`），
需要多步时可以写成 `--command "dotnet build && dotnet test"`。

```bash
#!/usr/bin/env bash
# test-and-handoff.sh：在后台服务中运行测试；失败时把输出保存到文件，并把修复请求投递给指定 Agent。
# 依赖：bash 3.2+、jq，crewterm 在 PATH 中。
# 用法：./test-and-handoff.sh <项目目录> <Agent 会话 ID> [测试命令]
set -euo pipefail

PROJECT=$(cd "${1:?用法: test-and-handoff.sh <项目目录> <会话 ID> [测试命令]}" && pwd)
AGENT=${2:?缺少 Agent 会话 ID}
TEST_CMD=${3:-dotnet test}
LOG="$PROJECT/.crewterm-run/test-failure.log"

ct() {
  if [ -n "${CREWTERM_STATE_DIR:-}" ]; then crewterm --state-dir "$CREWTERM_STATE_DIR" "$@"; else crewterm "$@"; fi
}

task=$(ct run --command "$TEST_CMD" --project "$PROJECT" --name Tests | jq -r .SessionId)
echo "后台任务 $task" >&2
while :; do
  json=$(ct task "$task")
  [ "$(jq -r .Task.Completed <<<"$json")" = true ] && break
  sleep 2
done

code=$(jq -r '.Task.ExitCode // "none"' <<<"$json")
cancelled=$(jq -r .Task.Cancelled <<<"$json")
error=$(jq -r '.Task.Error // ""' <<<"$json")
if [ "$code" = 0 ] && [ "$cancelled" = false ] && [ -z "$error" ]; then
  echo "测试通过" >&2
  exit 0
fi

mkdir -p "$(dirname "$LOG")"
jq -r .Task.Output <<<"$json" > "$LOG"
state=$(ct agent-state "$AGENT" | jq -r .Activity.State)
if [ "$state" = Working ] || [ "$state" = Blocked ]; then
  echo "Agent 当前为 $state，没有投递；失败输出在 $LOG" >&2
  exit 1
fi
ct agent-prompt "$AGENT" \
  --text "测试命令 $TEST_CMD 失败（退出码 $code），输出末尾保存在 $LOG。请阅读并修复，然后重新运行该命令确认。" > /dev/null
echo "已把失败交给 $AGENT" >&2
```

```powershell
# test-and-handoff.ps1：在后台服务中运行测试；失败时把输出保存到文件，并把修复请求投递给指定 Agent。
# 用法：./test-and-handoff.ps1 -Project <项目目录> -Agent <会话 ID> [-TestCommand 'dotnet test']
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Agent,
    [string]$TestCommand = 'dotnet test'
)
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$log = Join-Path $Project '.crewterm-run/test-failure.log'

# 运行 crewterm 并返回标准输出；非零退出码时抛出异常（crewterm 已在标准错误中写明原因）。
function Invoke-CrewTerm([string[]]$Arguments) {
    if ($env:CREWTERM_STATE_DIR) { $Arguments = @('--state-dir', $env:CREWTERM_STATE_DIR) + $Arguments }
    $output = & crewterm @Arguments
    if ($LASTEXITCODE -ne 0) { throw "crewterm $($Arguments -join ' ') 失败，退出码 $LASTEXITCODE" }
    $output -join "`n"
}

$taskId = (Invoke-CrewTerm @('run', '--command', $TestCommand, '--project', $Project, '--name', 'Tests') | ConvertFrom-Json).SessionId
Write-Host "后台任务 $taskId"
do {
    Start-Sleep -Seconds 2
    $task = (Invoke-CrewTerm @('task', $taskId) | ConvertFrom-Json).Task
} while (-not $task.Completed)

if ($task.ExitCode -eq 0 -and -not $task.Cancelled -and -not $task.Error) { Write-Host '测试通过'; exit 0 }

New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
Set-Content -LiteralPath $log -Value $task.Output -Encoding UTF8
$state = (Invoke-CrewTerm @('agent-state', $Agent) | ConvertFrom-Json).Activity.State
if ($state -in @('Working', 'Blocked')) { throw "Agent 当前为 $state，没有投递；失败输出在 $log" }
$text = "测试命令 $TestCommand 失败（退出码 $($task.ExitCode)），输出末尾保存在 $log。请阅读并修复，然后重新运行该命令确认。"
Invoke-CrewTerm @('agent-prompt', $Agent, '--text', $text) | Out-Null
Write-Host "已把失败交给 $Agent"
```

### 注意事项

- `run` 提交成功就返回 0，不代表任务成功；必须查看 `task ID` 的 `Completed`、`ExitCode`、`Cancelled` 与 `Error`。
- 任务的标准输入已关闭，需要交互的命令会失败或挂起到被取消。输出只保留末尾 262,144 个字符，其中可能含有命令输出的颜色控制序列；
  任务历史保留最近 100 条，同时运行的任务默认最多 4 个（`Settings: Concurrency and state directory` 可设为 1–32，重启后台服务后生效）。
- 输出写进文件再交给 Agent，是因为提示只能有一行，而且最多 32,768 个字符。Agent 读取项目外的文件时可能先请求你授权，所以示例写在项目内。
- 投递前检查了 Agent 不在 `Working` / `Blocked`：`agent-prompt` 本身会拒绝 `Blocked`，但不会等待正在工作的 Agent。
- 想等 Agent 修复完再重跑测试，可以接上上一节的 `wait_turn` / `Wait-Turn`，然后再次运行本脚本。

## 分离、重连与重启后续接

**目标**：关掉界面后 Agent 继续工作，稍后回到原来的布局；电脑重启后，从记录中续接原来的原生会话。

### 分离与重连

1. 按 F10，或 F9 → `Workspace: Detach (keep all sessions running)`。关闭承载界面的终端窗口也只结束界面进程，
   后台服务与其中的会话、任务继续运行。
2. 稍后在任意终端运行 `crewterm`（使用过 `--state-dir` 时要带上同一个目录），界面连接回原来的后台服务，标签页、分屏与会话绑定按项目恢复。
3. 不打开界面也可以先看看发生了什么：

```sh
crewterm sessions | jq -r '.Sessions[] | "\(.Id)  \(.Name)  \(.Activity.State)  未读 \(.UnreadCount)"'
crewterm notifications | jq -r '.Notifications[] | select(.Read | not) | "\(.Id)  \(.SessionId)  \(.Title)  \(.Reason)"'
```

```powershell
(crewterm sessions | ConvertFrom-Json).Sessions | Format-Table Id, Name, @{ n = 'State'; e = { $_.Activity.State } }, UnreadCount
(crewterm notifications | ConvertFrom-Json).Notifications | Where-Object { -not $_.Read } | Format-Table Id, SessionId, Title, Reason
```

列出不会把任何通知标为已读；回到界面后用 `Agent: Notifications` 逐条处理。

### 重启电脑后续接

重启前不需要做什么：会话目录、通知、收件箱、布局与任务历史都已持久保存。重启后：

1. 运行 `crewterm`，后台服务随之启动，原来的会话都成为休眠记录（`IsDormant: true`，状态 `Exited`），不出现在侧边栏中。
2. F9 → `Agent: Resume original conversation`：列表中可以恢复的记录显示 `ready`，否则显示原因；选中后在当前面板中恢复，沿用原来的会话 ID。
3. 不能恢复的记录：用 `Agent: Bind native conversation / resume command` 登记精确的原生 ID（在 Provider 自己的会话列表里找）
   或完整恢复命令；想开新对话用 `Agent: Restart saved command as new conversation`；不再需要的用 `Agent: Forget dormant session record`。

```sh
crewterm sessions --all true |
  jq -r '.Sessions[] | select(.IsDormant) | "\(.Id)\t\(.Name)\t\(.Provider // "custom")\t\(.CanResume)\t\(.ResumeReason)"'
crewterm session-resume <会话 ID>
crewterm session-bind <会话 ID> --provider codex --native-id <精确 UUID>
crewterm session-bind <会话 ID> --provider custom --resume-command "my-agent resume exact-conversation-id"
crewterm session-restart <会话 ID>
crewterm session-forget <会话 ID>
```

```powershell
(crewterm sessions --all true | ConvertFrom-Json).Sessions | Where-Object IsDormant |
    Format-Table Id, Name, Provider, CanResume, ResumeReason -Wrap
crewterm session-resume <会话 ID>
```

### 注意事项

- 回不来的东西：运行中的进程、终端屏幕与滚动历史、尚未完成的后台任务（完成的任务历史保留）。
- Codex 会话如果一轮都没有完成就被关闭，CrewTerm 拿不到它的 `thread-id`，只能手工绑定。
- 恢复后的会话从 `Unknown` 开始，并重新注入本次启动的回调；通知与收件箱中的旧消息仍在。
- CLI 恢复的会话以 100 × 30 启动，在界面面板中显示后改为面板尺寸。每个后台服务最多 32 个运行中的会话，超出时恢复失败。
- `crewterm stop`（或 `Workspace: Stop all sessions and background tasks`）结束全部会话与任务，F10 不会。
- 升级 CrewTerm 后旧版后台服务仍在运行时，界面会显示 “OLD SERVER”；保存工作后 `crewterm stop` 再打开。
- 详细的恢复规则见 [原生会话](native-sessions.md)。

## 用插件在完成或等待输入时提醒

**目标**：Agent 进入 `Done` 或 `Blocked` 时写一行日志，并在 Linux / macOS 上弹出系统通知；界面关闭时同样有效，因为插件由后台服务执行。

### 可用的事件

插件事件由后台服务排队执行，标准输入是一个 JSON 信封：`schemaVersion`（1）、`kind`（`event`）、`eventName`、`pluginId`、`commandId`
和 `payload`。`payload` 为 `{ stateDirectory, sessionId, detail }`，字段名都是小写开头：

| 事件 | 何时发出 | `payload.detail` 的字段 |
| --- | --- | --- |
| `agent.state` | 状态因显式上报、hook、输入或提示推断、识别到审批提示、进程退出而变化时 | `sessionId`、`state`、`source`、`reason`、`changedAt`、`revision` |
| `session.created` | 新建、恢复或重新开始会话成功后 | `id`、`name`、`command`、`workingDirectory`、`provider`、`nativeSessionId`、`resumeCommand`、`updatedAt` |
| `session.exited` | 进程退出或执行 `close` 后，每次运行一次 | `exitCode`、`activity`（字段同 `agent.state` 的 `detail`） |
| `message.received` | `message-send` 或 `agent-handoff` 保存消息后 | `id`、`fromSessionId`、`toSessionId`、`text`、`createdAt`、`acknowledged`、`inReplyTo` |

`state` 的值是 `"Done"`、`"Blocked"` 这样的枚举名。以下变化不发出 `agent.state`：会话启动时的 `Unknown`、后台服务启动时载入的休眠记录、
确认完成通知后的 `Idle`。没有专门的“通知创建”事件，用 `agent.state` 加上状态过滤即可。

### 插件文件

新建目录 `agent-alerts`，放入以下三个文件。默认调用 `/bin/sh alert.sh`（Linux / macOS，需要 `jq`），Windows 上通过 `windows`
替代调用改用系统自带的 Windows PowerShell 运行 `alert.ps1`。

`crewterm-plugin.json`：

```json
{
  "id": "local.agent-alerts",
  "name": "Agent alerts",
  "version": "1.0.0",
  "commands": [
    {
      "id": "on-state",
      "name": "Record Done and Blocked",
      "executable": "/bin/sh",
      "arguments": ["alert.sh"],
      "windows": {
        "executable": "powershell",
        "arguments": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "alert.ps1"]
      },
      "timeoutSeconds": 10,
      "maxOutputCharacters": 4096
    }
  ],
  "hooks": [
    { "event": "agent.state", "commandId": "on-state" }
  ]
}
```

`alert.sh`：

```sh
#!/bin/sh
# 读取 CrewTerm 事件信封，只处理 Done 与 Blocked：追加到 alerts.log，并尽量弹出系统通知。依赖 jq。
set -eu
envelope=$(cat)
state=$(printf '%s' "$envelope" | jq -r '.payload.detail.state // empty')
case "$state" in
  Done|Blocked) ;;
  *) exit 0 ;;
esac
session=$(printf '%s' "$envelope" | jq -r '.payload.sessionId')
reason=$(printf '%s' "$envelope" | jq -r '.payload.detail.reason // ""')
printf '%s\t%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$session" "$state" "$reason" >> alerts.log
if command -v notify-send > /dev/null 2>&1; then
  notify-send "CrewTerm: $state" "$session $reason" || true
elif command -v osascript > /dev/null 2>&1; then
  osascript -e 'on run argv' -e 'display notification (item 2 of argv) with title (item 1 of argv)' -e 'end run' \
    "CrewTerm: $state" "$session $reason" || true
fi
```

`alert.ps1`：

```powershell
# 读取 CrewTerm 事件信封，只处理 Done 与 Blocked，追加到 alerts.log。
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$envelope = [Console]::In.ReadToEnd() | ConvertFrom-Json
$detail = $envelope.payload.detail
if ($envelope.kind -ne 'event' -or $detail.state -notin @('Done', 'Blocked')) { exit 0 }
$line = "{0}`t{1}`t{2}`t{3}" -f [DateTime]::UtcNow.ToString('o'), $envelope.payload.sessionId, $detail.state, $detail.reason
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'alerts.log') -Value $line -Encoding UTF8
```

### 安装与测试

```sh
crewterm plugin-install --path ./agent-alerts
crewterm plugin-enable local.agent-alerts --enabled true
# 手动触发一次，payload 就是 --json 的内容：
crewterm plugin-event --event agent.state --json '{"sessionId":"demo","detail":{"state":"Done","reason":"手动测试"}}'
cat ./agent-alerts/alerts.log
crewterm plugin-history | jq '.PluginResults[-1]'
```

PowerShell 7.3 及以上默认会为原生程序正确转义参数中的双引号，可以照抄上面的 `--json` 写法；
Windows PowerShell 5.1 不会转义，需要把其中的每个 `"` 写成 `\"`。
界面中 F9 → `Plugins: Install local folder`、`Plugins: Enable / disable`、`Plugins: Execution history` 完成同样的操作。

### 注意事项

- 插件以你的用户身份运行，没有沙箱；安装后默认停用。安装时保存清单快照，修改清单要先移除再重新安装；脚本在原目录执行，修改后下次运行即生效。
- 同一状态可能连续出现两次 `agent.state`（例如屏幕识别到审批提示后又收到 hook 的 `PermissionRequest`），都满足过滤条件；
  需要去重时由脚本自己记录上一次的状态。
- 事件队列容量 32，按顺序处理；队列满时事件被丢弃，并在 `plugin-history` 中留下一条插件 ID 为 `daemon` 的失败记录。
  历史只保留最近 100 条；同时运行的插件进程最多 4 个，超时与失败都只记录在历史里，不影响后台服务。
- 事件信封最大 64 KiB；`message.received` 带有消息全文，很长的中文消息可能超过上限，这次插件运行会失败。
- 插件进程继承后台服务的环境变量（来自第一次启动后台服务的那个终端），`notify-send`、`osascript`、`jq` 要在其中的 `PATH` 上。
  这部分系统通知由脚本自己完成，CrewTerm 的测试没有覆盖。Windows 没有内置的命令行通知工具，CrewTerm 也不提供；
  需要弹窗时请自行选择工具接入 `alert.ps1`。
- 清单格式与上限见 [本地插件](local-plugins.md)。

## 并行子任务的隔离与汇总

**目标**：把一个需求拆成几个互不重叠的子任务，每个子任务一个 worktree、一个 Agent 并行处理，全部结束后得到一份汇总，再由你决定合并哪些分支。

### 选择隔离方式

| 需求 | 做法 |
| --- | --- |
| 各 Agent 不覆盖彼此的文件，但要互发消息、交接、在同一界面中查看 | 同一个状态目录，每个 Agent 一个 worktree |
| 一批实验与日常工作完全分开：会话、布局、收件箱、通知、插件、任务历史都独立 | 另一个 `--state-dir`，它有自己的后台服务 |
| 超过一个后台服务的上限（32 个会话、8 个并发等待） | 拆到多个状态目录；但不同状态目录的会话之间不能互发消息 |

用独立状态目录运行下面的脚本，并在结束后停止那个后台服务：

```sh
CREWTERM_STATE_DIR="$HOME/crewterm-batch" ./parallel.sh ~/work/app tasks.tsv
crewterm --state-dir "$HOME/crewterm-batch" --project ~/work/app     # 可选：打开这一批的工作台查看
crewterm --state-dir "$HOME/crewterm-batch" stop
```

```powershell
$env:CREWTERM_STATE_DIR = Join-Path $env:LOCALAPPDATA 'CrewTerm-batch'
./parallel.ps1 -Repo D:\work\app -TaskFile tasks.tsv
crewterm --state-dir $env:CREWTERM_STATE_DIR stop
Remove-Item Env:CREWTERM_STATE_DIR
```

### 汇总脚本

子任务文件每行一个子任务，分支名与任务描述之间用制表符分隔，例如：

```text
crew/export-csv	在 src/export 中实现 CSV 导出并补上测试
crew/export-json	在 src/export 中实现 JSON 导出并补上测试
```

脚本为每行创建 `仓库目录-分支名` 形式的 worktree，启动 Agent 并投递任务；之后每 10 秒读取一次全部会话的状态（不占用 `agent-wait` 的名额），
直到每个会话在投递之后都进入 `Done` 或 `Exited`；最后把状态、新提交与未提交的文件写进 `.crewterm-run/summary.md`。

```bash
#!/usr/bin/env bash
# parallel.sh：为每个子任务创建 worktree 并启动一个 Agent，全部结束后生成汇总。
# 依赖：bash 3.2+、git、jq，crewterm 在 PATH 中。
# 用法：./parallel.sh <仓库目录> <子任务文件>；子任务文件每行为“分支名<TAB>任务描述”。
set -euo pipefail

REPO=$(cd "${1:?用法: parallel.sh <仓库目录> <子任务文件>}" && pwd)
TASKS=${2:?缺少子任务文件}
AGENT_CMD=${AGENT_CMD:-claude}
RUN_DIR="$REPO/.crewterm-run"
TAB=$(printf '\t')

ct() {
  if [ -n "${CREWTERM_STATE_DIR:-}" ]; then crewterm --state-dir "$CREWTERM_STATE_DIR" "$@"; else crewterm "$@"; fi
}

# 脚本自己的就绪判断：屏幕非空，且间隔 2 秒的两次读取完全相同。
wait_ready() {
  local id=$1 previous="" current i
  for i in $(seq 1 30); do
    current=$(ct read "$id")
    if [ -n "$(printf '%s' "$current" | tr -d '[:space:]')" ] && [ "$current" = "$previous" ]; then
      return 0
    fi
    previous=$current
    sleep 2
  done
  echo "会话 $id 在 60 秒内没有稳定下来" >&2
  return 1
}

mkdir -p "$RUN_DIR"
: > "$RUN_DIR/sessions.tsv"
while IFS="$TAB" read -r branch task; do
  [ -n "$branch" ] || continue
  tree="$REPO-$(printf '%s' "$branch" | tr '/' '-')"
  git -C "$REPO" worktree add -b "$branch" "$tree" HEAD < /dev/null
  id=$(ct new --command "$AGENT_CMD" --project "$tree" --name "$branch" < /dev/null | jq -r .SessionId)
  printf '%s\t%s\t%s\t%s\n' "$id" "$branch" "$tree" "$task" >> "$RUN_DIR/sessions.tsv"
done < "$TASKS"

# 逐个等待就绪并投递任务，记下投递前的 Revision。
while IFS="$TAB" read -r id branch tree task; do
  wait_ready "$id" < /dev/null
  ct agent-state "$id" < /dev/null | jq -r .Activity.Revision > "$RUN_DIR/$id.revision"
  ct agent-prompt "$id" --text "$task。完成后把改动提交到当前分支 $branch。" < /dev/null > /dev/null
done < "$RUN_DIR/sessions.tsv"

# 每 10 秒读取一次全部会话的状态，直到每个会话在投递之后都进入 Done 或 Exited。
while :; do
  all=$(ct agent-state)
  pending=0
  while IFS="$TAB" read -r id branch tree task; do
    state=$(jq -r --arg id "$id" --argjson after "$(cat "$RUN_DIR/$id.revision")" \
      '.Activities[] | select(.SessionId == $id) | if .Revision > $after then .State else "Pending" end' <<<"$all")
    case $state in
      Done|Exited) ;;
      Blocked) echo "$branch（$id）正在等待输入，请在界面中处理" >&2; pending=1 ;;
      *) pending=1 ;;
    esac
  done < "$RUN_DIR/sessions.tsv"
  [ "$pending" = 0 ] && break
  sleep 10
done

{
  echo "# 并行子任务汇总"
  while IFS="$TAB" read -r id branch tree task; do
    echo
    echo "## $branch"
    echo
    echo "- 任务：$task"
    echo "- 会话：$id；worktree：$tree"
    echo "- 状态：$(ct agent-state "$id" < /dev/null | jq -r '.Activity | "\(.State)（\(.Source)）\(.Reason)"')"
    echo
    echo '~~~'
    git -C "$REPO" log --oneline "HEAD..$branch" < /dev/null
    git -C "$tree" status --short < /dev/null
    echo '~~~'
  done < "$RUN_DIR/sessions.tsv"
} > "$RUN_DIR/summary.md"
echo "汇总：$RUN_DIR/summary.md" >&2
```

```powershell
# parallel.ps1：为每个子任务创建 worktree 并启动一个 Agent，全部结束后生成汇总。
# 用法：./parallel.ps1 -Repo <仓库目录> -TaskFile <子任务文件> [-AgentCommand claude]
# 子任务文件为 UTF-8，每行为“分支名<TAB>任务描述”。
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$TaskFile,
    [string]$AgentCommand = 'claude'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$Repo = (Resolve-Path -LiteralPath $Repo).Path
$runDir = Join-Path $Repo '.crewterm-run'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

# 运行 crewterm 并返回标准输出；非零退出码时抛出异常（crewterm 已在标准错误中写明原因）。
function Invoke-CrewTerm([string[]]$Arguments) {
    if ($env:CREWTERM_STATE_DIR) { $Arguments = @('--state-dir', $env:CREWTERM_STATE_DIR) + $Arguments }
    $output = & crewterm @Arguments
    if ($LASTEXITCODE -ne 0) { throw "crewterm $($Arguments -join ' ') 失败，退出码 $LASTEXITCODE" }
    $output -join "`n"
}

# 脚本自己的就绪判断：屏幕非空，且间隔 2 秒的两次读取完全相同。
function Wait-Ready([string]$Id) {
    $previous = $null
    for ($i = 0; $i -lt 30; $i++) {
        $current = Invoke-CrewTerm @('read', $Id)
        if ($current.Trim() -and $current -ceq $previous) { return }
        $previous = $current
        Start-Sleep -Seconds 2
    }
    throw "会话 $Id 在 60 秒内没有稳定下来"
}

$jobs = @(foreach ($line in Get-Content -LiteralPath $TaskFile -Encoding UTF8) {
    if (-not $line.Trim()) { continue }
    $branch, $task = $line -split "`t", 2
    $tree = "$Repo-$($branch -replace '/', '-')"
    git -C $Repo worktree add -b $branch $tree HEAD | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败：$branch" }
    $id = (Invoke-CrewTerm @('new', '--command', $AgentCommand, '--project', $tree, '--name', $branch) | ConvertFrom-Json).SessionId
    [pscustomobject]@{ Id = $id; Branch = $branch; Tree = $tree; Task = $task; After = 0L }
})

# 逐个等待就绪并投递任务，记下投递前的 Revision。
foreach ($job in $jobs) {
    Wait-Ready $job.Id
    $job.After = (Invoke-CrewTerm @('agent-state', $job.Id) | ConvertFrom-Json).Activity.Revision
    Invoke-CrewTerm @('agent-prompt', $job.Id, '--text', "$($job.Task)。完成后把改动提交到当前分支 $($job.Branch)。") | Out-Null
}

# 每 10 秒读取一次全部会话的状态，直到每个会话在投递之后都进入 Done 或 Exited。
do {
    Start-Sleep -Seconds 10
    $activities = (Invoke-CrewTerm @('agent-state') | ConvertFrom-Json).Activities
    $pending = 0
    foreach ($job in $jobs) {
        $activity = $activities | Where-Object { $_.SessionId -eq $job.Id }
        if ($activity.Revision -gt $job.After -and $activity.State -in @('Done', 'Exited')) { continue }
        if ($activity.State -eq 'Blocked') { Write-Warning "$($job.Branch)（$($job.Id)）正在等待输入，请在界面中处理" }
        $pending++
    }
} while ($pending -gt 0)

$summary = foreach ($job in $jobs) {
    $activity = (Invoke-CrewTerm @('agent-state', $job.Id) | ConvertFrom-Json).Activity
    ''
    "## $($job.Branch)"
    ''
    "- 任务：$($job.Task)"
    "- 会话：$($job.Id)；worktree：$($job.Tree)"
    "- 状态：$($activity.State)（$($activity.Source)）$($activity.Reason)"
    ''
    '~~~'
    git -C $Repo log --oneline "HEAD..$($job.Branch)"
    git -C $job.Tree status --short
    '~~~'
}
$summaryPath = Join-Path $runDir 'summary.md'
Set-Content -LiteralPath $summaryPath -Value (@('# 并行子任务汇总') + $summary) -Encoding UTF8
Write-Host "汇总：$summaryPath"
```

### 注意事项

- 拆分与合并都由你完成：CrewTerm 不检查子任务是否重叠，也不合并分支。看过汇总后在主仓库中 `git merge` 或 `git cherry-pick`，再删除 worktree。
- 子任务会改到同一批文件时，并行只会制造冲突；这种情况宁可顺序执行。
- 脚本只认 `Done` 与 `Exited` 为结束。用没有 hook 的 Agent 时，要让它在完成时用 `agent-report` 上报
  （见 [用 agent-report 自行上报](agent-integration.md#用-agent-report-自行上报)），否则脚本会一直等下去。
- `Blocked` 的会话由你在界面中处理；轮询期间脚本只会反复提醒。也可以配合上一节的插件，在 `Blocked` 时收到系统通知。
- 每个 Agent 都是一个终端会话，一个后台服务最多 32 个。
- 不同状态目录的后台服务互相独立：脚本、界面和停止命令都必须使用同一个 `--state-dir`，在其中运行的终端里 `CREWTERM_STATE_DIR` 会自动指向它。
- 更多界面操作见 [使用指南](user-guide.md)，术语见 [术语表](glossary.md)。
