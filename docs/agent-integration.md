# Agent 与 Provider 集成

本文写给想弄清 CrewTerm 如何判断各个 Agent 在做什么、要接入自己的 Agent CLI 或编写协作脚本的用户：内置 Provider 的差异、
语义状态从哪里来、Claude Code 与 Codex 的回调如何接入，以及消息、提示投递、交接与等待各自做什么、不做什么。
命令的完整参数见 [CLI 使用说明](cli-guide.md)，可以照做的配方见 [多 Agent 工作流实战](workflows.md)。

## 目录

- [内置 Provider](#内置-provider)
- [语义状态模型](#语义状态模型)
- [Claude Code 的接入](#claude-code-的接入)
- [Codex 的接入](#codex-的接入)
- [回调程序 agent-hook](#回调程序-agent-hook)
- [用 agent-report 自行上报](#用-agent-report-自行上报)
- [协作语义](#协作语义)
- [添加自定义 Agent](#添加自定义-agent)
- [原生会话恢复概要](#原生会话恢复概要)
- [已知限制](#已知限制)

## 内置 Provider

Provider 是 CrewTerm 对某一种 Agent CLI 的适配：怎样识别它、能否接入它的生命周期回调、怎样精确恢复它的原生会话。
内置配置由 `AgentCatalog` 定义，F3 列表与 `crewterm doctor` 显示的就是这六项：

| 配置 ID | 名称 | 可执行文件 | 自动语义状态来源 |
| --- | --- | --- | --- |
| `claude` | Claude Code | `claude` | 调用级 `--settings` hook、输入推断、审批提示识别 |
| `codex` | Codex | `codex` | 调用级 `notify`（只报告一轮结束）、输入推断、审批提示识别 |
| `opencode` | OpenCode | `opencode` | 输入推断、审批提示识别 |
| `gemini` | Gemini CLI | `gemini` | 输入推断、审批提示识别 |
| `copilot` | GitHub Copilot CLI | `copilot` | 输入推断、审批提示识别 |
| `shell` | Shell | Windows：`PATH` 中的 `pwsh`，找不到时为 `System32\WindowsPowerShell\v1.0\powershell.exe`；Linux / macOS：`$SHELL`，未设置时为 `/bin/sh` | 审批提示识别 |

任何会话都可以用 `agent-report` 显式上报状态，进程退出时都记为 `Exited`；各来源的规则见 [状态从哪里来](#状态从哪里来)。
CrewTerm 不安装、不登录这些 CLI，也不保存任何模型 API Key。

### 查找与识别

- **查找**：F3 与 `doctor` 只在 `PATH` 的绝对路径条目中查找（`.` 之类的相对条目被忽略）。Windows 上没有扩展名的名称先按 `PATHEXT`
  （默认 `.COM;.EXE;.BAT;.CMD`）尝试，因此能找到 npm 生成的 `.cmd` 启动脚本；Linux / macOS 上只接受有执行权限的普通文件。
  找不到时 F3 显示 `not installed`。
- **启动**：F3 用找到的绝对路径构造命令。`crewterm new --command` 命令开头的程序名由后台服务（daemon）按同样的原则解析，
  不在项目目录中查找；Windows 上 `.cmd` / `.bat` 经 `cmd.exe /d /s /c` 启动，Linux / macOS 上经 `/bin/sh -c "exec …"` 启动。
  `doctor` 与 F3 使用你当前终端的 `PATH`，`new` 使用后台服务启动时继承的 `PATH`。
- **识别 Provider**：`new --provider NAME` 显式指定；否则取命令第一个参数的文件名（去掉目录和扩展名，不区分大小写），
  恰好是 `claude`、`codex`、`gemini`、`opencode`、`copilot` 之一时就是该 Provider。`npx claude`、`my-claude` 这类包装命令不会被识别；
  Shell 与自定义命令没有 Provider（`sessions` 输出中的 `Provider` 为 `null`）。

### 原生会话标识

| Provider | 标识格式 | CrewTerm 如何得到它 | 自动生成的恢复命令 | 验证范围 |
| --- | --- | --- | --- | --- |
| Claude Code | 完整 UUID | 新对话启动时追加预先分配的 `--session-id`；命令中已有的精确 `--resume`、`-r` 或 `--session-id`；hook 收到带新标识的 `SessionStart` 时换绑 | `claude --resume UUID` | 本机 Claude Code 2.1.310 的帮助与无付费启动、本地参数 fixture |
| Codex | 完整 UUID | 记录中还没有标识时，取第一次 `agent-turn-complete` notify 的 `thread-id`；命令中的 `codex resume UUID` | `codex resume UUID` | 本机 codex-cli 0.163.0 的帮助与无付费启动、本地参数 fixture |
| OpenCode | `ses_` 开头 | 命令中的 `--session` 或 `-s` | `opencode --session ses_ID` | 只按官方文档构造，有命令构造测试；本机未安装 |
| Gemini CLI | 完整 UUID | 命令中的 `--resume`、`-r` 或 `--session-id` | `gemini --resume UUID` | 同上 |
| GitHub Copilot CLI | 完整 UUID | 命令中的 `--resume`、`-r` 或 `--session-id` | `copilot --resume=UUID` | 同上 |
| Shell / 自定义 | 可选，只作记录 | 只能显式登记 | 登记的完整恢复命令 | — |

“验证范围”来自 [原生会话](native-sessions.md) 与 0.2 的验证记录；任何 Provider 都没有用付费模型回合实测过对话历史的保存与回读。

- 任何 Provider 都可以用 `new --native-id` 或 `session-bind --native-id` 明确提供标识。对 Claude，`new --native-id UUID` 把该 UUID
  作为新对话的 `--session-id`；对其他已知 Provider，则把启动命令改写为恢复该对话的命令。
- `--continue`、`-c`（Codex 的 `-c` 是配置选项，不在此列）、`--last`、不带值的 `--resume` 都不是精确标识，不会被记录；
  含 `--fork-session`、`--fork` 或 `--no-session-persistence` 的启动不声称拥有任何标识，需要等真正的新标识出现后再绑定。

### 恢复能力

`sessions --all true` 中每个会话的 `CanResume` 与 `ResumeReason` 来自 `ProviderResumeCapability`：

| `CanResume` | `ResumeReason` | 含义 |
| --- | --- | --- |
| `true` | `Explicit resume command is registered.` | 已登记显式恢复命令，恢复时优先使用 |
| `true` | `Exact native conversation ID and verified launch options are recorded.` | 有精确标识，原启动命令能被受限语法解析 |
| `false` | `Native conversation ID has not been captured. …` | 已知 Provider，但还没有标识（例如 Codex 还没有完成过一轮） |
| `false` | `No verified native resume adapter. …` | 没有 Provider，或不是五个已知 Provider 之一 |
| `false` | 其他文字 | 标识格式不对，或原命令无法安全拆分（未知选项、包装脚本、Shell 语法等）；消息写明具体原因 |

## 语义状态模型

语义状态描述 Agent 在做什么，与终端进程是否存在相互独立。每个会话有一条当前观测（`Activity`），保存在状态目录的
`agent-activity.json` 中；后台服务是唯一的写入方，重启后保留。

### 六种状态

| 状态 | 侧边栏标签 | 含义 |
| --- | --- | --- |
| `Unknown` | `?` | 没有可靠证据：会话刚登记或刚启动，还没有收到任何显式事件 |
| `Working` | `WORK` | 有显式证据表明一轮任务正在进行 |
| `Blocked` | `WAIT` | 需要你输入或决定：权限请求、识别到的审批提示、Provider 报告的失败等 |
| `Done` | `DONE*` / `DONE` | 收到了显式的完成事件，对应的完成通知还没有被确认 |
| `Idle` | `IDLE` | 完成已被确认，或收到了显式的空闲事件 |
| `Exited` | `EXIT` | 终端进程已经退出，或只剩休眠记录；只描述进程，不代表任务完成 |

CLI 的 `--state` 不区分大小写（可以写 `done,blocked`），JSON 输出中是 `"Done"` 这样的枚举名。侧边栏中进程已退出的会话一律显示
`EXIT <退出码>`；`DONE*` 表示该会话还有未读项。休眠记录不在侧边栏中，用 `sessions --all true` 或 `Agent: Resume original conversation` 查看。

### 状态从哪里来

| 事件 | Source | 新状态 | 产生通知 |
| --- | --- | --- | --- |
| 会话启动（新建、恢复、重新开始） | `process` | `Unknown` | 否 |
| 后台服务启动时载入的休眠记录 | `process` | `Exited` | 否 |
| 终端进程退出，或执行 `close` | `process` | `Exited` | 否 |
| 已知 Provider 的会话收到以回车结尾的输入（在界面中按 Enter，或 `send` 带回车） | `input` | `Working` | 否 |
| `agent-prompt` / `agent-handoff` 写入提示 | `prompt` | `Working` | 否 |
| 屏幕上出现已知的审批提示 | `screen` | `Blocked` | 是 |
| Claude `SessionStart`、`SessionEnd` | `hook` | `Idle` | 否 |
| Claude `UserPromptSubmit` | `hook` | `Working` | 否 |
| Claude `PermissionRequest`、`StopFailure` | `hook` | `Blocked` | 是 |
| Claude `Stop` | `hook` | `Done`；`background_tasks` 或 `session_crons` 为非空数组时为 `Working` | 为 `Done` 时是 |
| Claude `Notification`：`permission_prompt` / `idle_prompt` | `hook` | `Blocked` / `Idle` | 为 `Blocked` 时是 |
| Codex notify `agent-turn-complete` | `hook` | `Done` | 是 |
| `agent-report` | 调用方指定，默认 `api` | 指定的状态 | 进入 `Blocked` / `Done` 时是 |
| 确认会话当前的完成通知 | `ack` | `Idle` | 否 |

- **通知**：只有状态从其他状态变为 `Blocked`（标题 `Needs input`）或 `Done`（标题 `Completed`）时才生成一条未读通知；
  同一状态下只是原因变化不会重复通知。状态、来源、原因都与当前观测相同的事件视为重复，不提交，`Revision` 不变。
- **审批提示识别**：后台服务保留每个会话最近 4,096 个字符的输出，去掉 ANSI 控制序列（光标换行、定位类序列换成换行）后，
  在行首（允许前导空白）不区分大小写地匹配 `Do you want to proceed?`、`Would you like to proceed?`、`Allow this command?` 三句。
  会话已退出、或当前观测的来源是 `api` / `hook` 时不识别；任何显式上报都会清空这段缓存。它对所有会话生效，包括 Shell。
- **输入推断**：只对 Provider 为 `claude`、`codex`、`gemini`、`opencode`、`copilot` 的会话生效，输入必须以 `\r` 或 `\r\n` 结尾；
  状态先记为 `Working` 再写入终端，写入失败且期间没有别的变化时恢复原状态。提示投递则对所有会话都记为 `Working`。
- **hook 事件**：终端已关闭或进程已退出时忽略；Provider 必须与会话记录一致；来自另一个原生对话的事件被忽略，
  只有 Claude 的 `SessionStart`、或记录中还没有标识时才换绑到新标识。
- **确认**：确认完成通知后，只有会话仍是 `Done` 且没有其他未读的完成通知时才变为 `Idle`。

### Source、Reason 与 Revision

| 字段 | 含义 |
| --- | --- |
| `State` | 上表中的语义状态 |
| `Source` | 证据来源标签，最长 64 个字符。CrewTerm 自己使用 `process`、`input`、`prompt`、`screen`、`hook`、`ack`；`agent-report` 省略 `--source` 时为 `api`，也可以写任意标签 |
| `Reason` | 给人看的原因，最长 4,096 个字符，例如 `Claude Stop: native turn finished` |
| `ChangedAt` | 观测产生的时间（UTC） |
| `Revision` | 产生这条观测时状态目录的全局版本号 |

- `Revision` 是整个状态目录共用的递增计数器，持久保存，后台服务重启后继续。任何会话的状态变化、通知的生成与已读、消息的发送与确认
  都会让它增加，所以同一会话相邻两条观测的 `Revision` 通常不连续。脚本应保存最后看到的值，再用 `agent-wait --after-revision` 等待更新的观测。
- 会话启动后的第一条观测形如 `Terminal started; <接入说明>; waiting for explicit Agent activity`，`<接入说明>` 写明这次启动是否接入了
  自动回调以及原因（见 [Claude Code 的接入](#claude-code-的接入) 与 [Codex 的接入](#codex-的接入)），之后的状态变化会替换它。
- 界面中 F9 → `Agent: Explain selected status` 显示当前面板会话的 State、Source、Reason、Changed 与 Revision。

### 通知、未读与已读

- 未读数 `UnreadCount` = 该会话未读的通知数 + 发给它、尚未确认的消息数。
- 读取永远不会改变已读状态：`notifications`、`message-list`、`agent-state` 和侧边栏刷新都只读。
- 通知只能显式标为已读：CLI 用 `notification-read NOTIFICATION_ID`；界面中 F9 → `Agent: Notifications` 选中一条（先切换到该会话，
  再标为已读），或 `Agent: Mark selected as read`（当前面板会话的全部未读通知）。
- 把 `Blocked` 的通知标为已读后会话仍是 `Blocked`，也不代表批准了任何工具调用；审批只能在该 Agent 的终端里完成。
- 状态目录最多保留最近 200 条通知、500 条消息。

### 侧边栏优先级

| 优先级 | 条件（按顺序取第一个满足的） |
| --- | --- |
| 300 | 当前状态为 `Blocked` |
| 200 | 有未读的完成通知 |
| 100 | 有发给它、尚未确认的消息 |
| 50 | 当前状态为 `Working` |
| 0 | 其他 |

侧边栏、`sessions` 与 `sessions --all true` 按优先级从高到低、同级按启动时间排列，输出中的 `Priority` 就是这个数值；
不带 ID 的 `agent-state` 也按优先级排列。出现新的 `Done` / `Blocked` 且有未读项时，状态栏显示 `<名称>: <原因>  ·  F9 → Agent: Notifications`。

### 为什么普通输出不会被判为“完成”

- 进入 `Done` 只有三条路：Claude 的 `Stop` hook、Codex 的 `agent-turn-complete` notify、`agent-report --state done`。
- 终端输出只用来识别三句审批提示，只可能得到 `Blocked`；“All tests passed”“Done!”之类的文字不会改变状态。
- 没有空闲计时：长时间没有输出不等于完成，状态保持原样。
- 进程退出记为 `Exited`，不生成完成通知，因为 Agent 可能是崩溃了或被你关闭了。
- 进程存活与否不推导任何状态：刚启动、还没有事件的 Agent 是 `Unknown`，不是 `Working` 或 `Idle`。

## Claude Code 的接入

条件：会话的 Provider 是 `claude`，且命令能被 CrewTerm 的受限语法解析（只含已知选项、至多一个初始提示词，没有 Shell 运算符、
包装脚本或多余的位置参数）。

### 启动与恢复时注入的内容

每次启动、恢复或重新开始，CrewTerm 只对这一次调用做两处改动：

1. 新对话追加预先分配的 `--session-id <UUID>`；恢复时命令改为 `claude --resume <记录的 UUID>`，保留已验证的选项，去掉旧选择器和初始提示词
   （登记了显式恢复命令时改用该命令）。
2. 在状态目录的 `provider-hooks` 下写入 `<会话 ID>.json`，并追加 `--settings "<该文件>"`。

命令中有 `--` 时，追加的选项插在 `--` 之前。设置文件为 `SessionStart`、`UserPromptSubmit`、`PermissionRequest`、`Stop`、
`StopFailure`、`Notification`、`SessionEnd` 七个事件注册同一个回调；下面是 `SessionStart` 一项（格式化后，实际文件只有一行），
其余六个事件的值完全相同：

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "<CrewTerm 安装目录>/crewterm",
            "args": ["--state-dir", "<状态目录>", "agent-hook", "--provider", "claude", "--id", "<会话 ID>"],
            "timeout": 3,
            "onFailure": "continue"
          }
        ]
      }
    ]
  }
}
```

`command` 是与后台服务同目录的 `crewterm`（Windows 上为 `crewterm.exe`）的完整路径。接入成功时，启动后的 Reason 包含
`Claude lifecycle hooks connected for this invocation`。

### 事件映射

| hook 事件 | 状态 | Reason |
| --- | --- | --- |
| `SessionStart` | `Idle` | `Claude SessionStart: native conversation started or resumed` |
| `UserPromptSubmit` | `Working` | `Claude UserPromptSubmit: provider received a prompt` |
| `PermissionRequest` | `Blocked` | `Claude PermissionRequest: permission requested; review the terminal and decide explicitly` |
| `Notification`（`permission_prompt`） | `Blocked` | `Claude Notification: provider notification: permission_prompt` |
| `Notification`（`idle_prompt`） | `Idle` | `Claude Notification: provider notification: idle_prompt` |
| `Stop` | `Done` | `Claude Stop: native turn finished` |
| `Stop`（仍有后台任务或定时任务） | `Working` | `Claude Stop: foreground turn stopped; background work or scheduled tasks remain` |
| `StopFailure` | `Blocked` | `Claude StopFailure: provider reported a failure; inspect the terminal` |
| `SessionEnd` | `Idle` | `Claude SessionEnd: native conversation ended or switched; this is not process exit` |

- 其他 `Notification` 类型与未列出的事件被忽略。CrewTerm 只读取 `hook_event_name`、`session_id`、`notification_type`、
  `background_tasks`、`session_crons` 这几个字段，提示词与回复内容不会被保留。
- `idle_prompt` 可以让 `Done` 直接变成 `Idle`，此时未读的完成通知仍然保留（优先级仍为 200），需要你显式确认。

### 何时跳过

| 情况 | 启动后 Reason 中的说明 |
| --- | --- |
| 命令中已有 `--settings`、`--settings=…`、`--bare` 或 `--safe-mode` | `Existing Claude settings or disabled hooks were preserved; automatic hook is not connected` |
| 命令无法被受限语法解析（包装脚本、未知选项、Shell 运算符、多个位置参数等） | `Automatic hook not connected: <具体原因>` |
| 设置文件路径含当前平台的 Shell 展开字符（Windows：`%`、`!`、`"`；Linux / macOS：`$`、反引号、`\`、`"`） | `Hook settings path contains shell expansion syntax; use explicit reports` |

跳过时命令保持原样，状态只能来自输入推断、审批提示识别与 `agent-report`。因已有 `--settings` 等选项而跳过时，`--session-id`
仍照常预分配；因命令无法解析而跳过时，原生标识也无法自动记录，需要登记显式恢复命令。

### 与你已有配置的关系

- 不修改 Claude Code 的任何用户或项目配置。设置文件只在 CrewTerm 状态目录中，每次启动覆盖，`session-forget` 时删除。
- 原生会话目录保存的是你的原始命令，不含注入的 `--session-id` 与 `--settings`，所以恢复或重新开始时会重新生成。
- 你在命令里自己传了 `--settings` 时，CrewTerm 不合并也不替换，只是这次不接入自动回调。
- 回调成功时不向标准输出写任何内容，因此不会向 Claude Code 返回权限决定；设置中写的是 `onFailure: continue`。

## Codex 的接入

条件：会话的 Provider 是 `codex`，命令能被受限语法解析，并且你没有在命令行或用户配置中设置 `notify`。

### 启动与恢复时注入的内容

CrewTerm 追加 `-c "notify=['<crewterm 完整路径>','agent-hook','--provider','codex']"`（命令中有 `--` 时插在它之前；路径含单引号时
改用 `'''…'''` 包裹）。Codex 在一轮结束时把 JSON 作为最后一个参数调用它，回调程序从终端环境变量 `CREWTERM_SESSION_ID` 与
`CREWTERM_STATE_DIR` 得到会话与状态目录。接入时的说明为
`Codex turn-complete notify connected; Working remains inferred from submitted input`。

Codex 普通启动没有可以预先分配的会话 ID。恢复时命令改为 `codex resume <UUID>` 并保留已验证的选项（登记了显式恢复命令时改用该命令），
同样重新注入 notify。

### 事件映射

- 只识别 `type` 为 `agent-turn-complete` 的通知，映射为 `Done`，Reason 为 `Codex notify agent-turn-complete: the native turn finished`；
  其他类型被忽略。
- `thread-id` 必须是完整 UUID。记录中还没有原生标识时用它登记；已有不同的标识时忽略该事件
  （`Hook from a different native conversation ignored`）。
  在 Codex 内部切换到另一个对话后，需要用 `session-bind` 明确改绑。
- `Working` 只来自输入推断（来源 `input` / `prompt`），不是 Codex 的确认；`Blocked` 只来自审批提示识别或 `agent-report`。
- CrewTerm 不使用需要你在 Codex `/hooks` 中信任的生命周期 hook，也不会替你绕过 hook trust。

### 何时跳过

| 情况 | 启动后 Reason 中的说明 |
| --- | --- |
| 命令行中已有 notify 覆盖（`-c notify=…`、`--config notify=…` 及其 `=` 写法） | `Existing Codex notify override was preserved; automatic hook is not connected` |
| `CODEX_HOME`（未设置时为用户主目录下的 `.codex`）中的 `config.toml` 或任一 `*.config.toml` 有行首的 `notify =` | `Existing Codex notify in user or profile configuration was preserved; automatic hook is not connected` |
| 上述某个文件超过 1 MiB | `Codex config is too large to inspect; existing notify configuration is preserved` |
| 无法读取这些文件 | `Codex config could not be inspected; existing notify configuration is preserved` |
| CrewTerm 可执行文件路径含 Shell 展开字符或 `'''` | `Hook executable path cannot be represented safely in the notify invocation; use explicit reports` |
| 命令无法被受限语法解析 | `Automatic hook not connected: <具体原因>` |

- 项目目录中 `.codex/config.toml` 的 `notify` 不影响接入，因为 Codex 本身忽略项目级的 notify。
- `CODEX_HOME` 取自后台服务进程的环境变量，也就是第一次启动后台服务的那个终端的环境。
- CrewTerm 只读取这些配置文件，从不修改它们。

## 回调程序 agent-hook

`crewterm agent-hook` 是给 Provider 调用的被动入口，由上面两种接入自动生成；脚本或 Agent 自己上报请用 `agent-report`。

| 项目 | 行为 |
| --- | --- |
| 参数 | `--provider`、`--state-dir`、`--id`；缺省时依次取终端环境变量 `CREWTERM_PROVIDER`、`CREWTERM_STATE_DIR`、`CREWTERM_SESSION_ID` |
| 负载 | `--json` 的值；Provider 为 Codex 时取唯一的位置参数；否则读取标准输入直到结束。最多 65,536 个字符，必须是 JSON 对象 |
| 连接 | 直接连接该状态目录的后台服务（Windows 命名管道 / Unix 域套接字，只允许当前用户），500 毫秒内连不上即失败；读取与转发总共限时 2.5 秒 |
| 后台服务未运行 | 以退出码 1 结束，不会启动新的后台服务 |
| 无法识别的事件 | 不联系后台服务，直接以 0 退出 |
| 输出 | 成功时标准输出与标准错误都为空，从不输出审批或权限决定；失败时在标准错误写 `CrewTerm: <原因>` |

后台服务收到回调后按 [状态从哪里来](#状态从哪里来) 中的规则更新状态，来源记为 `hook`。

## 用 agent-report 自行上报

没有 hook 的 Agent（OpenCode、Gemini CLI、GitHub Copilot CLI、自定义 CLI）、Shell 里的长任务和你自己的脚本，都可以显式上报语义状态：

```sh
crewterm agent-report <会话 ID> --state <状态> [--source 标签] [--reason 原因]
```

- `--state` 必填；`--source` 默认 `api`，`--reason` 默认 `Explicit CLI report`。来源最长 64 个字符且不能含控制字符；
  原因最长 4,096 个字符，可以换行。选项的值不能以 `--` 开头。
- 上报是声明，不是验证：CrewTerm 不检查模型或构建是否真的完成。
- 来源为 `api` 或 `hook` 时，审批提示识别会暂停，直到来源变成别的值（例如下一次输入推断或提示投递）；想保留识别，就用自己的标签，
  例如 `--source agent`。
- 与当前观测完全相同的上报不产生变化；进入 `Done` / `Blocked` 会生成通知，并像其他状态变化一样触发 `agent.state` 插件事件。

CrewTerm 启动的每个终端都带有这些环境变量：

| 变量 | 值 |
| --- | --- |
| `CREWTERM_SESSION_ID` | 本会话的 CrewTerm 会话 ID |
| `CREWTERM_STATE_DIR` | 所属后台服务的状态目录（绝对路径） |
| `CREWTERM_EXE` | 与后台服务同目录的 `crewterm` 可执行文件 |
| `CREWTERM_PROVIDER` | 识别出的 Provider；没有时不设置 |

因此终端里的命令可以上报自己的状态；Agent 执行的工具命令只要继承了这些变量，也可以这样做：

```sh
"$CREWTERM_EXE" --state-dir "$CREWTERM_STATE_DIR" agent-report "$CREWTERM_SESSION_ID" \
  --state done --source agent --reason "迁移脚本已执行，42 个测试全部通过"
```

```powershell
& $env:CREWTERM_EXE --state-dir $env:CREWTERM_STATE_DIR agent-report $env:CREWTERM_SESSION_ID `
    --state blocked --source agent --reason '需要确认是否删除旧表'
```

一定要带上 `--state-dir`：`agent-report` 与其他普通子命令一样，连不上后台服务时会启动一个；省略它就会连接（必要时启动）
默认状态目录的后台服务，而那里没有这个会话。

下面的包装脚本在 CrewTerm 终端里运行任意命令：开始时报告 `Working`，成功报告 `Done`，失败报告 `Blocked`（失败需要你决定下一步）。

```sh
#!/bin/sh
# crew-run.sh：在 CrewTerm 终端中运行一条命令，并把开始与结果上报为语义状态。
# 用法：./crew-run.sh make test
: "${CREWTERM_SESSION_ID:?请在 CrewTerm 终端中运行}"
[ "$#" -gt 0 ] || { echo "用法：crew-run.sh <命令> [参数…]" >&2; exit 2; }
report() {
  "$CREWTERM_EXE" --state-dir "$CREWTERM_STATE_DIR" agent-report "$CREWTERM_SESSION_ID" \
    --state "$1" --source script --reason "$2" > /dev/null
}
report working "开始：$*"
if "$@"; then
  report done "完成：$*"
else
  code=$?
  report blocked "失败（退出码 $code）：$*"
  exit "$code"
fi
```

```powershell
# crew-run.ps1：在 CrewTerm 终端中运行一条命令，并把开始与结果上报为语义状态。
# 用法：./crew-run.ps1 dotnet test
if (-not $env:CREWTERM_SESSION_ID) { throw '请在 CrewTerm 终端中运行' }
if ($args.Count -eq 0) { throw '用法：crew-run.ps1 <命令> [参数…]' }
function Send-Report([string]$State, [string]$Reason) {
    & $env:CREWTERM_EXE --state-dir $env:CREWTERM_STATE_DIR agent-report $env:CREWTERM_SESSION_ID `
        --state $State --source script --reason $Reason | Out-Null
}
$command = $args -join ' '
$program = $args[0]
$rest = @($args | Select-Object -Skip 1)
Send-Report working "开始：$command"
& $program @rest
$code = $LASTEXITCODE
if ($code -eq 0) { Send-Report done "完成：$command" }
else { Send-Report blocked "失败（退出码 $code）：$command"; exit $code }
```

让没有 hook 的 Agent 自己上报，可以把上面的 `agent-report` 命令写进它读取的项目说明，要求它在完成或需要你决定时运行。
是否照做取决于模型，执行命令也可能先要你批准；CrewTerm 不会自动识别这类 Agent 的完成。

## 协作语义

所有协作操作都要求明确指定会话：CrewTerm 不自动选择收件人、不广播、不拆分任务。两个 ID 都是 CrewTerm 会话 ID，不是 Provider 的原生 ID。

### 定向消息

`message-send --from A --to B --text T`；界面 F9 → `Agent: Send message`（发件方是当前面板的会话，收件人从未退出的会话中选）。

- **做什么**：校验双方都是已登记的会话（CLI 也接受已退出或休眠的记录），把消息存入 `agent-activity.json`，计入收件方的未读数与优先级，
  触发 `message.received` 插件事件；返回 `Message` 与 `Delivered: false`。消息在后台服务重启后保留。
- **不做什么**：不向任何终端输入文字。Agent 本身看不到收件箱，除非它在自己的终端里运行
  `"$CREWTERM_EXE" --state-dir "$CREWTERM_STATE_DIR" message-list "$CREWTERM_SESSION_ID"`。缺少 `--to` 时直接报错，不会替你挑选收件人。
- **限制**：正文非空、最多 32,768 个字符，除换行、回车、制表符外不能含控制字符；只保留最近 500 条。

### 回复关联

`message-send … --reply-to MESSAGE_ID`。

- **做什么**：要求被回复的消息正是由本次的收件方发给本次的发件方，结果写入新消息的 `InReplyTo`。
- **不做什么**：不确认原消息，不生成线程视图。界面没有填写回复的入口，只在收件箱详情中显示 `In reply to`。

### 确认

消息用 `message-ack <收件会话 ID> --message MESSAGE_ID`，界面在 `Agent: Inbox` 中打开一条消息、关闭详情时确认；通知用 `notification-read`。

- **做什么**：只有请求中给出的会话正是消息的收件方时才接受，否则报 `Message belongs to another recipient`。
  已确认或不存在的消息返回失败（CLI 退出码 1）。
- **不做什么**：列出或查看不会确认任何东西。这是对“由谁确认”的一致性校验，不是身份认证：同一用户的任何进程都可以带着任意会话 ID 调用 CLI。

### 提示投递

`agent-prompt <会话 ID> --text T`。

- **做什么**：校验正文非空白、最多 32,768 个字符、除 `\r`、`\n`、`\t` 外没有控制字符（Ctrl+C、ESC 等都会被拒绝）；目标必须是运行中的会话，
  进程未退出，语义状态不是 `Blocked`。去掉末尾的换行后，仍含换行的多行文本只有在目标当前启用了 bracketed paste 时才作为一次粘贴写入，
  否则拒绝；最后补一个回车。写入前把状态记为 `Working`（来源 `prompt`）。返回 `Delivered: true` 与当前的 `Activity`。
- **不做什么**：不排队等待 Agent 空闲，目标处于 `Working` 时也会立即写入，CLI 如何处理工作中收到的输入由它自己决定；
  不确认模型已收到、理解或执行；不投递到休眠会话；不能代替审批。注意原始输入 `send` 不做 `Blocked` 检查，等同于你在终端里按键。

### 交接

`agent-handoff --from A --to B --text T`；界面 F9 → `Agent: Handoff prompt`（发件方是当前面板的会话）。

- **做什么**：先按提示投递的规则检查目标（运行中、不是 `Blocked`、正文合法），再保存一条从 A 到 B 的定向消息并触发 `message.received`，
  然后把同样的文字作为提示投递给 B。成功时返回 `Message` 与 `Delivered: true`。
- **不做什么**：消息保存之后投递失败（例如多行文字而目标未启用 bracketed paste）时，消息不会撤回：请求以失败结束（CLI 退出码 1，
  错误以 `Message was stored, but terminal delivery failed:` 开头），消息留在 B 的收件箱里，盲目重试会产生重复消息。
  交接只传递你写的文字，不传递文件或上下文，也不等待 B 完成。

### 等待

`agent-wait <会话 ID> --state S1,S2 [--timeout-ms N] [--after-revision R]`；界面 F9 → `Agent: Wait for selected state`。

- **做什么**：等待会话进入任一目标状态；给出 `--after-revision` 时还要求观测的 `Revision` 严格大于 R。当前观测已满足条件时立即返回。
  超时默认 10,000 毫秒，必须大于 0 且不超过 60,000；每个后台服务同时最多 8 个等待，超出时立即失败
  （`Maximum 8 concurrent Agent waits; retry after a wait completes`）。等待不轮询，由状态提交唤醒；成功返回 `Activity`。
- **不做什么**：超时算失败（CLI 退出码 1，`Timed out waiting for Agent state: <ID>`），不会返回当时的状态；单次不能超过 60 秒，
  更长的等待需要脚本循环；不检查进程是否还在，等待 `done` 时最好同时等待 `exited`；不会让没有状态来源的 Agent 产生状态。
  界面版本只能选一个状态和 1–60 秒，不带 revision 条件，在后台等待，期间界面可以继续使用。

## 添加自定义 Agent

CrewTerm 支持三种方式运行内置配置之外的 Agent：

1. **一次性命令**：F3 → `Custom command…`，输入命令行（默认 Windows 为 `cmd.exe`，其他平台为 `$SHELL` 或 `/bin/sh`）和侧边栏名称。
2. **保存为配置**：F9 → `Agent: Save custom CLI / SSH profile`，依次输入显示名、可执行文件名或完整路径（默认 `ssh`）和可选参数。
   配置出现在 F3 列表中，`doctor` 也会检查它；`Agent: Remove custom profile` 删除（内置配置不能删除）。
3. **命令行**：`crewterm new --command "…" --project PATH --name NAME`，可另加 `--provider NAME`、`--native-id ID`、
   `--resume-command COMMAND`。

配置保存在状态目录的 `agent-profiles.json`：一个 JSON 数组，字段为 `Id`、`Name`、`Executable`、`Arguments`、`IsBuiltIn`（区分大小写）。
界面为新配置生成随机 `Id`；`Id` 与内置配置相同（例如 `claude`）的条目会覆盖内置配置，这只能通过手工编辑文件实现。
`Id` 只允许字母、数字、`-` 和 `_`，`Executable` 不能含引号或换行；文件损坏时 CrewTerm 拒绝读取并保留原文件。

自定义 Agent 能得到的能力：

| 能力 | 自定义 Agent |
| --- | --- |
| 终端、分屏、标签页、收件箱、交接、等待、插件事件 | 与内置 Agent 相同 |
| 语义状态 | 审批提示识别、`agent-report`、提示投递和进程退出；输入推断只对五个已知 Provider 生效 |
| 自动回调 | 没有。只有 `claude` 与 `codex` 有经过验证的适配器，也没有插件接口可以添加新的适配器 |
| 原生恢复 | 用 `new --resume-command` 或 `session-bind --provider custom --resume-command` 登记完整的恢复命令 |

- 包装脚本启动的若其实是 Claude 或 Codex，可以用 `--provider claude` 等显式指定，从而获得输入推断与标识格式校验；
  但包装命令不能被受限语法解析，自动回调不会接入（Reason 中为 `The executable is a wrapper or differs from the provider; …`），
  恢复也需要显式恢复命令。
- 通过 `ssh` 运行的远程 Agent：远程主机上的进程连接不到本机后台服务，无法在远程一侧调用 `agent-report`；
  状态只能来自审批提示识别、提示投递和你在本机执行的上报。

## 原生会话恢复概要

- CrewTerm 会话 ID（12 位十六进制）与 Provider 的原生会话 ID 分开保存在 `native-sessions.json`；关闭会话、重启后台服务或电脑后记录都保留，
  后台服务启动时把它们载入为休眠记录（状态 `Exited`）。
- 恢复（`session-resume`，界面 `Agent: Resume original conversation`）只使用登记的精确标识或显式恢复命令；重新开始（`session-restart`）
  明确开启新对话；遗忘（`session-forget`）只删除 CrewTerm 的记录与相关活动资料，需要先关闭会话；关闭（`close`）结束进程、保留记录。
- 恢复与重新开始沿用原来的 CrewTerm 会话 ID，重新注入本次启动的回调，状态从 `Unknown` 开始；对仍在运行的会话执行恢复不会再启动一份。
- 绝不用 `--last`、名称或前缀猜测要恢复的对话；`CanResume` 为 `false` 时按 `ResumeReason` 用 `session-bind` 补登记。

参数保留规则、受限语法和各 Provider 的依据见 [原生会话](native-sessions.md)。

## 已知限制

- 只有 Claude Code 与 Codex 有自动回调；OpenCode、Gemini CLI、GitHub Copilot CLI 与自定义 Agent 的完成只能靠 `agent-report`。
- Codex 只报告一轮结束：没有权限请求事件，`Working` 只是输入推断，原生标识要等第一轮结束后才能拿到。
- 审批提示识别只认三句英文提示且要求位于行首。其他确认界面（登录、信任目录、选择菜单等）不会变成 `Blocked`，
  而任何程序在行首打印这三句话都会。
- 提示投递不排队、不检查 Agent 是否忙碌；多行提示要求目标已启用 bracketed paste，而 CLI 没有查询该状态的命令（`read` 只返回文字）。
- `send` 是原始输入，不检查 `Blocked`；“只有收件方可确认”只校验请求中的会话 ID，不验证调用者身份。
- 单次等待最长 60 秒，每个后台服务同时最多 8 个等待、最多 32 个终端会话；通知只保留 200 条、消息 500 条。
- 语义状态、收件箱与通知按状态目录隔离，不同 `--state-dir` 的会话之间不能互发消息。
- 终端会话继承后台服务启动时的环境变量；之后修改 `PATH`、`CODEX_HOME` 等，要在保存工作后 `crewterm stop` 并重新打开才会生效。
- 适配基于上表列出的已验证版本；Provider 将来改变参数或事件格式时，自动接入可能失效。
- CrewTerm 不自动分工、不自动合并修改、不隐式广播，也不替任何 Agent 审批。
