# 配置与数据参考

本文面向需要查看或修改 CrewTerm 0.3 设置、排查状态文件、备份与迁移工作台或编写脚本的使用者，逐项列出启动参数、环境变量、
状态目录中的每个文件与字段、模板格式和全部数量上限。设计原因见 [架构](architecture.md)，子命令的用法见 [CLI 使用说明](cli-guide.md)。

## 目录

- [启动参数](#启动参数)
- [环境变量](#环境变量)
- [状态目录](#状态目录)
- [设置](#设置)
- [Agent 配置](#agent-配置)
- [命令模板与流水线模板](#命令模板与流水线模板)
- [布局与项目列表](#布局与项目列表)
- [原子写入与冲突](#原子写入与冲突)
- [日常维护](#日常维护)
- [数量上限](#数量上限)

## 启动参数

```
crewterm [--project PATH] [--state-dir PATH]   打开工作台（等同于 crewterm ui）
crewterm --server [--state-dir PATH]           以后台服务（daemon）身份运行，通常由客户端自动启动
crewterm --help | -h | help                    打印帮助（与工作台 F1 的内容相同）
crewterm --version | version                   打印 “CrewTerm 0.3.0”
crewterm doctor [--state-dir PATH]             列出各 Agent 配置找到的程序，不连接后台服务
crewterm stop [--state-dir PATH]               停止后台服务；没有运行时只提示 “CrewTerm daemon is not running.”
crewterm <子命令> ... [--state-dir PATH]        其余子命令见 cli-guide.md
```

| 选项 | 适用于 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `--project PATH` | 工作台 | 不指定 | 打开前把该目录登记到项目列表（已登记则复用原条目）并选中它。目录必须存在，否则报错退出；相对路径按当前目录解析。不指定时选中上次的项目，没有记录时选中列表中的第一个 |
| `--state-dir PATH` | 全部 | 见[位置与解析规则](#位置与解析规则) | 状态目录。每个目录有自己的后台服务；同一套工作台的每条命令都要使用同一个目录 |
| `--help`、`-h` | — | — | 打印帮助后以 0 退出 |
| `--version` | — | — | 打印版本后以 0 退出 |
| `--server` | — | — | 以后台服务身份运行，直到收到 `stop` 请求 |

解析规则（`CrewTermCommandLine`）：

- 第一个不以 `--` 开头的参数是子命令（不区分大小写），省略时为 `ui`；其后的非选项参数是位置参数，例如会话 ID。
- `--help`、`-h`、`--version`、`--server` 是不带值的开关，本身就是子命令，也可以写成 `help`、`version`、`server`。
- 其余 `--名称` 都必须跟一个值，值不能以 `--` 开头，因此无法传入以 `--` 开头的文本；布尔值写成 `--all true`、`--enabled false`。
  选项名不区分大小写，同一选项出现多次时以最后一次为准。
- 退出码：0 表示成功；1 表示失败，原因以 `CrewTerm: ` 开头写入标准错误。工作台在已接受的终端输入未能全部送达时也返回 1。

后台服务的启动：

- 工作台与需要后台服务的子命令（`doctor`、`stop`、`agent-hook` 除外）连接不到后台服务时，自动以
  `crewterm --server --state-dir <绝对路径>` 启动它，工作目录为程序所在目录，然后最多等待约 3 秒（30 次 × 100 毫秒）。
  没有启动成功时提示 `Session server did not start. Inspect server-error.log in <目录>`。
- Windows 上后台服务不继承调用方的句柄、不创建窗口。Linux / macOS 上服务启动后创建新会话（`setsid`）并把标准输入输出改接
  `/dev/null`，此后的错误只写入 `server-error.log`；直接在交互式 Shell 中运行 `crewterm --server` 而无法创建新会话时，
  服务忽略 `SIGHUP`、`SIGINT`、`SIGQUIT`。
- 同一状态目录只能运行一个后台服务（`server.lock`），第二个实例启动失败并把原因写入 `server-error.log`。
- 连接到协议版本低于 2 的旧版服务时，客户端只允许基础操作，见 [从旧后台服务切换](cli-guide.md#从旧后台服务切换)。

`doctor` 先输出版本、操作系统与状态目录，再逐行列出每个 Agent 配置（含自定义配置）找到的程序路径，找不到时显示 `not installed`。

## 环境变量

### CrewTerm 读取的环境变量

| 变量 | 读取者 | 平台 | 用途 |
| --- | --- | --- | --- |
| `PATH` | 工作台与 `doctor`（发现 Agent）；后台服务（解析会话的启动命令） | 全部 | 只使用其中的绝对路径条目，`.` 等相对条目与空条目被忽略，见[可执行文件的查找规则](#可执行文件的查找规则) |
| `PATHEXT` | 工作台与 `doctor`（发现 Agent） | Windows | 程序名没有扩展名时依次尝试的扩展名；未设置时为 `.COM;.EXE;.BAT;.CMD`。后台服务启动会话时不读取它 |
| `SHELL` | 工作台与 `doctor`（内置 Shell 配置）；工作台（F3 自定义命令的默认值） | Linux / macOS | 未设置时为 `/bin/sh` |
| `CODEX_HOME` | 后台服务（启动 Codex 会话时） | 全部 | Codex 配置目录，未设置时为用户主目录下的 `.codex`。其中的 `config.toml` 或 `*.config.toml` 已经配置了 `notify` 时，CrewTerm 保留它并且不接入回调；文件超过 1 MiB 或无法读取时同样不接入 |
| `CREWTERM_STATE_DIR` | `crewterm agent-hook` | 全部 | 命令行没有 `--state-dir` 时使用的状态目录 |
| `CREWTERM_SESSION_ID` | `crewterm agent-hook` | 全部 | 命令行没有 `--id` 时使用的会话 ID |
| `CREWTERM_PROVIDER` | `crewterm agent-hook` | 全部 | 命令行没有 `--provider` 时使用的 Provider |
| `XDG_DATA_HOME` | .NET（计算默认状态目录） | Linux | 见[位置与解析规则](#位置与解析规则) |
| `CREWTERM_TEST_EXE` | 测试 | 全部 | 只被测试读取，指向要验证的发布产物，见 [开发者指南](development.md) |

除 `agent-hook` 外，子命令不会从环境变量读取状态目录：在会话中用脚本访问同一套非默认工作台时，要显式写
`--state-dir "$CREWTERM_STATE_DIR"`（见[多套工作台](#多套工作台)）。CrewTerm 没有用于修改设置、上限或日志的环境变量。

### 为子进程设置的环境变量

后台服务进程继承启动它的那个客户端（第一个需要服务的工作台或 CLI 命令）的环境，此后不再变化；会话、后台任务和插件进程都在此基础上派生。
修改 `PATH` 等变量后，要 `crewterm stop` 再重新打开，新的值才会进入之后启动的会话。

**终端会话**（`ConPtySession`、`UnixPtySession`、`DaemonHost.NativeSessions`）在继承的环境上做以下修改：

| 变量 | 值 | 平台 | 说明 |
| --- | --- | --- | --- |
| `TERM` | `xterm-256color` | 全部 | 总是覆盖继承来的值 |
| `COLORTERM` | `truecolor` | 全部 | 总是覆盖继承来的值 |
| `NoDefaultCurrentDirectoryInExePath` | `1` | Windows | 会话中的 cmd.exe（包括 npm 安装的 `.cmd` 启动脚本）不再从当前目录查找程序 |
| `LANG` | Linux 为 `C.UTF-8`，macOS 为 `en_US.UTF-8` | Linux / macOS | 只在环境中没有 `LANG`、`LC_ALL`、`LC_CTYPE` 任何一个时补上 |
| `CREWTERM_SESSION_ID` | 会话 ID（12 位十六进制） | 全部 | 回调与脚本用它指明自己是哪个会话 |
| `CREWTERM_STATE_DIR` | 状态目录的绝对路径 | 全部 | |
| `CREWTERM_EXE` | 与后台服务同目录的 `crewterm.exe`（Windows）或 `crewterm` 的绝对路径 | 全部 | 调用与后台服务同一版本的程序 |
| `CREWTERM_PROVIDER` | 识别出的 Provider，例如 `claude`、`codex` | 全部 | 没有识别出 Provider 时删除这个变量，不会从服务的环境继承 |

Provider 来自 `crewterm new --provider`，或从程序文件名识别（`claude`、`codex`、`gemini`、`opencode`、`copilot`），见
[Agent 集成指南](agent-integration.md)。Claude Code 的回调命令自带 `--state-dir`、`--provider claude`、`--id` 参数；Codex 的 `notify`
只带 `agent-hook --provider codex`，状态目录与会话 ID 取自 Codex 进程继承的 `CREWTERM_STATE_DIR`、`CREWTERM_SESSION_ID`。

**后台任务**（`TaskRunner`）继承后台服务的环境，标准输入已关闭，不设置 `TERM` 等终端变量，也不设置 `NoDefaultCurrentDirectoryInExePath`。
Windows 上命令经 `cmd.exe` 执行：先 `chcp 65001` 切换到 UTF-8，再通过一个名为 `CREWTERM_TASK_COMMAND_<32 位十六进制>` 的变量
把命令原样交给内层 cmd；Linux / macOS 上经 `/bin/sh -c` 执行，不添加变量。任务命令中的程序由 cmd 或 `/bin/sh` 按它们自己的规则查找，
不经过下文[可执行文件的查找规则](#可执行文件的查找规则)；Windows 上的 cmd 因此仍会先在当前目录（项目目录）中查找程序。

**插件进程**（`LocalPluginRunner`）继承后台服务的环境，不添加也不删除任何变量；输入通过标准输入的 JSON 传递，
见 [本地插件](local-plugins.md)。

**Git**（工作台进程中运行，`GitService`）额外设置 `GIT_TERMINAL_PROMPT=0`（不弹出凭据提示）与 `GIT_OPTIONAL_LOCKS=0`
（只读命令不刷新索引，避免与 Agent 争抢 `index.lock`）。

## 状态目录

### 位置与解析规则

| 系统 | 默认状态目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\CrewTerm` |
| Linux | `$XDG_DATA_HOME/CrewTerm`，未设置时为 `~/.local/share/CrewTerm` |
| macOS | `~/Library/Application Support/CrewTerm` |

默认位置是 .NET `LocalApplicationData` 目录下的 `CrewTerm`（`StateFiles.DefaultDirectory`）。

- `--state-dir PATH` 指定其他目录。相对路径按每条命令执行时的当前目录解析，在不同目录中执行会指向不同的工作台；脚本中请使用绝对路径。
  Windows 上大小写不同或带末尾分隔符的同一路径对应同一个后台服务。
- 目录不存在时，由客户端在启动后台服务之前、以及后台服务在启动时创建：Linux / macOS 上权限为 `0700`，
  Windows 上继承上级目录的权限（默认位置受用户配置文件目录的 ACL 保护）。已存在的目录保持原有权限，CrewTerm 不会修改；
  `checkpoints/`、`provider-hooks/` 等子目录按系统默认权限创建，由上级目录保护。
- `crewterm doctor` 不经过上述步骤：目录不存在时它按系统默认权限（umask）创建目录。在 Linux / macOS 上第一次使用新的
  `--state-dir` 时，先打开工作台或运行一条连接后台服务的子命令，或者自己用 `mkdir -m 700` 创建。
- 状态目录不能位于要创建检查点的项目内部，否则创建检查点时报 `Checkpoint storage must be outside the selected project.`。
- 客户端与后台服务之间的连接只允许当前用户：Windows 使用命名管道 `CrewTerm-<用户名>-<路径哈希>`，不在目录中产生文件；
  Linux / macOS 使用状态目录中的 Unix 域套接字 `server.sock`，路径的 UTF-8 长度超过 100 字节时改用系统临时目录中以 `crewterm-`
  加哈希命名的套接字。传输与帧格式见 [后台服务协议](protocol.md)。

### 目录内容

文件在第一次用到时才出现。

```
<状态目录>/
├─ projects.json             项目列表
├─ workspace.json            设置、命令模板与各项目的布局
├─ agent-profiles.json       自定义 Agent 配置（内置配置不落盘）
├─ pipeline-templates.json   流水线模板
├─ task-history.json         已完成后台任务的历史
├─ native-sessions.json      会话目录：启动命令、Provider 与原生会话标识
├─ saved-sessions.json       运行中会话的快照（旧版格式，保留用于兼容）
├─ agent-activity.json       语义状态、通知与收件箱
├─ local-plugins.json        本地插件登记
├─ provider-hooks/           Claude Code 单次启动的回调配置
│  └─ <会话 ID>.json
├─ checkpoints/              文件检查点
│  └─ <项目路径哈希>/
│     ├─ <检查点 ID>.meta.json
│     └─ <检查点 ID>.snapshot.json
├─ server.lock               后台服务独占锁
├─ server.sock               后台服务的套接字（仅 Linux / macOS）
└─ server-error.log          后台服务错误日志
```

| 文件 | 写入方 | 何时读取 | 手动编辑 | 删除的后果 |
| --- | --- | --- | --- | --- |
| `projects.json` | 工作台 | 工作台启动时 | 关闭所有工作台后可以 | 侧边栏变空；项目目录与运行中的会话不受影响，选中某个会话时会重新登记它的工作目录。已保存的布局按项目 ID 索引，重新添加的项目得到新 ID，从新布局开始 |
| `workspace.json` | 工作台 | 工作台启动时；后台服务启动时（只读取并发数） | 关闭所有工作台后可以；并发数在后台服务重启后生效 | 恢复默认：并发数 4、没有命令模板、每个项目从一个 `Terminal` 标签页开始 |
| `agent-profiles.json` | 工作台 | 工作台启动时、每次打开 F3 列表、`doctor` | 可以，下次打开 F3 即生效 | 自定义配置消失，内置配置不受影响 |
| `pipeline-templates.json` | 工作台 | 每次打开流水线相关菜单 | 可以，下次打开菜单即生效 | 流水线模板消失 |
| `task-history.json` | 后台服务 | 后台服务启动时 | 先停止后台服务 | 后台服务重启后历史中只剩删除之后完成的任务；运行中的任务不受影响 |
| `native-sessions.json` | 后台服务 | 频繁读取：会话列表、终端输入、回调与恢复等请求都会读取 | 先停止后台服务；登记原生会话请用 `session-bind` | 休眠记录与已登记的原生会话标识全部丢失，无法再从 CrewTerm 恢复这些对话（Provider 自己的对话数据仍在）。后台服务启动时若此文件不存在而 `saved-sessions.json` 存在，会从后者重新导入记录 |
| `saved-sessions.json` | 后台服务 | 只在后台服务启动且 `native-sessions.json` 不存在时用于迁移 | 没有必要 | 没有影响，下一次会话变化时重新写出 |
| `agent-activity.json` | 后台服务 | 后台服务启动时 | 先停止后台服务，否则会被下一次状态变化覆盖 | 语义状态、通知与全部消息丢失；后台服务运行中删除时，下一次状态变化会从内存重新写出 |
| `local-plugins.json` | 后台服务 | 后台服务启动时与每次插件操作 | 用 `plugin-*` 子命令或 F9 中的插件命令修改；手动编辑前先停止后台服务 | 插件登记全部消失，插件目录中的文件不受影响 |
| `provider-hooks/<会话 ID>.json` | 后台服务 | Claude Code 经 `--settings` 读取 | 不要编辑，每次启动都会重新生成 | 下次启动、恢复或重新开始该会话时重新生成；`session-forget` 时自动删除 |
| `checkpoints/` | 工作台 | 列出、预览、恢复检查点时 | 不要编辑快照；可以删除整个检查点 | 被删除的检查点无法再预览或恢复；CrewTerm 从不自动删除检查点 |
| `server.lock` | 后台服务 | — | 不需要 | 后台服务未运行时删除没有影响，下次启动重新创建；运行中不要删除 |
| `server.sock` | 后台服务 | 客户端连接时 | 不需要 | 运行中删除后客户端（包括 `stop`）无法再连接这个后台服务 |
| `server-error.log` | 后台服务 | — | 可以随时清空或删除 | 只丢失日志；文件不会自动轮换，会一直增长 |

进程在写入过程中被强制结束时，目录中可能残留 `.crewterm-<32 位十六进制>.tmp` 临时文件，在 CrewTerm 没有运行时可以删除。

### 各文件的格式

JSON 状态文件的共同规则：

- UTF-8 编码，CrewTerm 写入时不带 BOM、带缩进（检查点与 `provider-hooks/` 中的文件除外）；手动保存时也请使用无 BOM 的 UTF-8。
- 属性名与源码中的名称一致（`Id`、`Name` 等首字母大写），读取时**区分大小写**：写错大小写的字段读不到，通常会使文件被判为无效。
  不认识的字段被忽略。不支持注释与末尾逗号。
- 时间是带时区偏移的 ISO 8601 字符串，CrewTerm 写入的时间为 UTC，例如 `"2026-10-26T08:00:00.1234567+00:00"`。
- CrewTerm 生成的 ID 都是不带连字符的十六进制 GUID：项目、任务、Agent 配置、流水线、检查点、通知与消息为 32 位，
  会话、标签页与布局节点为 12 位。

`projects.json`、`workspace.json`、`agent-profiles.json`、`pipeline-templates.json` 的字段分别见 [布局与项目列表](#布局与项目列表)、
[设置](#设置)、[自定义 Agent 与 SSH 配置](#自定义-agent-与-ssh-配置)、[流水线模板](#流水线模板)。其余文件：

**`task-history.json`**：数组，按开始时间从新到旧，最多 100 条。每条记录：

| 字段 | 说明 |
| --- | --- |
| `Id`、`Name`、`Command`、`WorkingDirectory` | 任务 ID、名称、命令与工作目录，都不能为空。流水线的 `Command` 是每行一个的 `<步骤名>: <命令>` |
| `StartedAt`、`FinishedAt` | 开始与结束时间，结束时间不能早于开始时间 |
| `ExitCode` | 退出码；流水线为最后执行的步骤的退出码。为 `null` 表示没有得到退出码（排队时被取消，或进程未能启动） |
| `Output` | 标准输出与标准错误合并后的最后 262,144 个字符 |
| `Cancelled`、`OutputTruncated` | 是否被取消、输出是否被截断 |
| `Succeeded`、`Duration` | 写入时附带的派生字段（未取消且退出码为 0；耗时），读取时忽略 |

**`native-sessions.json`**：数组，每个会话一条，会话关闭后保留，直到 `session-forget`。字段：`Id`（会话 ID，不能重复）、`Name`、
`Command`（最初的启动命令）、`WorkingDirectory`，以及可选的 `Provider`、`NativeSessionId`（Provider 自己的对话标识）、
`ResumeCommand`（显式登记的恢复命令）、`UpdatedAt`。`Id`、`Name`、`Command`、`WorkingDirectory` 不能为空，命令中不能有 NUL。
各字段的含义与恢复规则见 [原生会话](native-sessions.md)。

**`saved-sessions.json`**：数组，后台服务在会话创建、关闭、遗忘与回调更新标识时写出的运行中会话列表，字段与 `crewterm sessions`
输出的 `Sessions` 元素相同。当前版本以 `native-sessions.json` 为准，这个文件只写不读，仅在迁移时使用。

**`agent-activity.json`**：一个对象。

| 字段 | 说明 |
| --- | --- |
| `Revision` | 全局版本号，每次变化加 1 |
| `Activities` | 每个会话的当前语义状态：`SessionId`、`State`（`Unknown`、`Working`、`Blocked`、`Done`、`Idle`、`Exited` 之一，字符串）、`Source`、`Reason`、`ChangedAt`、`Revision` |
| `Notifications` | 最近 200 条通知：`Id`、`SessionId`、`State`、`Title`（`Needs input` 或 `Completed`）、`Reason`、`CreatedAt`、`Read`、`Source` |
| `Messages` | 最近 500 条消息：`Id`、`FromSessionId`、`ToSessionId`、`Text`、`CreatedAt`、`Acknowledged`、`InReplyTo` |

后台服务启动时把会话目录中的每条记录登记为 `Exited`（休眠，没有终端进程）。状态的含义见 [Agent 集成指南](agent-integration.md)。

**`local-plugins.json`**：数组，每个插件一条：`Id`、`Name`、`Version`、`SourceDirectory`（插件目录，必须是完整的绝对路径）、
`Enabled`，以及 `Manifest`——安装时 `crewterm-plugin.json` 的完整快照，字段为 `Id`、`Name`、`Version`、`Commands`、`Hooks`。
`Id` 必须与 `Manifest.Id` 相同、不区分大小写不能重复，清单按安装时的规则重新校验。修改插件目录中的清单后要移除并重新安装，
见 [本地插件](local-plugins.md)。插件的执行历史只保存在后台服务的内存中，不写入这个文件。

**`provider-hooks/<会话 ID>.json`**：只为 Claude Code 生成，以 `--settings "<路径>"` 附加在这一次启动的命令上。内容是一个不缩进的
`hooks` 对象，为 `SessionStart`、`UserPromptSubmit`、`PermissionRequest`、`Stop`、`StopFailure`、`Notification`、`SessionEnd`
各登记一个回调：

```json
{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"/opt/crewterm/crewterm","args":["--state-dir","/home/me/.local/share/CrewTerm","agent-hook","--provider","claude","--id","a1b2c3d4e5f6"],"timeout":3,"onFailure":"continue"}]}]}}
```

（示例只列出一个事件。）启动命令已经带有 `--settings`、`--bare` 或 `--safe-mode`、命令无法可靠解析，或者文件路径含有 Shell 会展开的字符时，
不生成这个文件。Codex 的回调通过 `-c notify=[...]` 写在命令行中，不产生文件。

**`checkpoints/<项目路径哈希>/`**：子目录名是项目根目录绝对路径的 SHA-256（Windows 上先转为大写）。每个检查点两个文件，
各写入一次、之后不再修改：

| 文件 | 内容 |
| --- | --- |
| `<ID>.meta.json` | `Id`、`Label`（名称）、`Root`（项目根目录）、`CreatedAt`、`FileCount`、`TotalBytes`；检查点列表只读取这些文件 |
| `<ID>.snapshot.json` | `Info`（同上）与 `Files`：以 `/` 分隔的相对路径到 Base64 文件内容的映射 |

检查点包含项目中除 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`artifacts`、`.crewterm` 目录与符号链接之外的全部文件。
删除一个检查点时把它的两个文件一起删除；只删除 `.meta.json` 时它不再出现在列表中。恢复时会重新校验快照中的路径与上限。

**`server.lock`** 是空文件，运行中的后台服务以独占方式打开它。**`server-error.log`** 每次追加一条记录（异常的堆栈可能占多行）：
后台服务启动或运行失败记为 `<时间> <异常>`，处理 Agent 状态时的错误记为 `<UTC 时间> Agent activity: <异常>`。
工作台自身的错误不写入这个文件。

## 设置

设置保存在 `workspace.json`（`WorkspaceSettings`）。

| 字段 | 类型 | 默认值 | 取值范围 | 说明 |
| --- | --- | --- | --- | --- |
| `LastProjectId` | 字符串或 `null` | `null` | `projects.json` 中的项目 ID | 上次选中的项目；指向不存在的项目时改为选中第一个项目 |
| `LastSessionId` | 字符串或 `null` | `null` | 会话 ID | 上次活动面板的会话；不带 `--project` 启动、且该项目还没有保存的布局时，第一个面板显示这个会话 |
| `SplitTerminals` | 布尔 | `false` | — | 当前标签页是否有多个面板；只在一个项目布局都没有时决定第一个布局是否分屏（兼容旧版） |
| `MaximumConcurrentTasks` | 整数 | `4` | 1–32 | 后台服务同时执行的后台任务数，超出的任务排队。后台服务只在启动时读取 |
| `Templates` | 数组 | `[]` | — | 命令模板，见[命令模板](#命令模板) |
| `ProjectLayouts` | 对象 | `{}` | — | 各项目的标签页与分屏，见[布局与项目列表](#布局与项目列表) |

```json
{
  "LastProjectId": "5d1c0c3f8a4b4e5f9b0a1c2d3e4f5a6b",
  "LastSessionId": "a1b2c3d4e5f6",
  "SplitTerminals": false,
  "MaximumConcurrentTasks": 4,
  "Templates": [
    {
      "Name": "Test",
      "Command": "dotnet test",
      "WorkingDirectory": "D:\\work\\my-app"
    }
  ],
  "ProjectLayouts": {}
}
```

在界面中修改：

- 并发数：F9 → `Settings: Concurrency and state directory`，输入 1–32。保存后对话框会显示当前状态目录。新的并发数在后台服务重启后生效：
  `Workspace: Stop all sessions and background tasks`（或 `crewterm stop`）后重新打开；停止会结束全部会话与任务。
- 命令模板：F7 中的 `Save command template` 与 `Remove command template`。
- 其余字段由界面自动维护，不需要手动修改。

文件在加载与保存时都会校验：并发数超出 1–32、`Templates` 或 `ProjectLayouts` 为 `null`、某个模板缺少字段、某个布局不合法，
都会使整个文件无效（工作台无法启动；后台服务也因为读取并发数而无法启动）。

## Agent 配置

### 内置 Agent

| ID | 名称 | 可执行文件 |
| --- | --- | --- |
| `claude` | Claude Code | `claude` |
| `codex` | Codex | `codex` |
| `opencode` | OpenCode | `opencode` |
| `gemini` | Gemini CLI | `gemini` |
| `copilot` | GitHub Copilot CLI | `copilot` |
| `shell` | Shell | Windows：`PATH` 中的 `pwsh`，找不到时为系统目录下的 `WindowsPowerShell\v1.0\powershell.exe`；Linux / macOS：`$SHELL`，未设置时为 `/bin/sh` |

内置配置都不带参数，也不写入磁盘。F3 列表与 `doctor` 先列出内置配置，再列出自定义配置，并显示实际找到的程序路径。
从 F3 选择已安装的配置时，启动命令是“找到的绝对路径 + 参数”；显示 `not installed` 的配置不能启动。CrewTerm 不安装、不登录这些 CLI，
也不保存任何 API Key。各 Provider 的状态接入与恢复见 [Agent 集成指南](agent-integration.md)。

### 可执行文件的查找规则

CrewTerm 在两个地方查找程序，规则相近但不完全相同。

**发现**（F3 列表与 `doctor`，`ExecutableLocator`）：

1. 名称含目录分隔符或是绝对路径时，只检查这一个位置；相对路径按工作台（或 `doctor`）进程的当前目录解析。
2. 否则依次检查 `PATH` 的每个条目：去掉首尾空白与引号后只使用完整的绝对路径，`.` 等相对条目与空条目被忽略，
   因此不会在当前目录或项目目录中找到程序。在第一个有匹配的条目处停止。
3. Windows：名称没有扩展名时，先按 `PATHEXT` 的顺序（未设置时为 `.COM;.EXE;.BAT;.CMD`）加上扩展名尝试，再尝试不带扩展名的同名文件；
   名称带扩展名时只检查该文件。文件存在即可。npm 全局安装的 CLI 同时有无扩展名的 Unix 脚本与 `.cmd` 启动脚本，按这个顺序会找到 `.cmd`。
4. Linux / macOS：只接受至少有一个执行位（属主、属组或其他人任一）的文件。

**启动**（后台服务创建会话时，`TerminalCommand`）。命令行的第一个词是程序（以 `"` 开头时取到下一个 `"`），其余部分原样作为参数：

1. 绝对路径直接使用；含目录分隔符的相对路径（`./build.sh`、`.\build.cmd`）按会话的工作目录（项目目录）解析。
2. 裸程序名只在 `PATH` 的绝对路径条目中查找，不查找项目目录。
3. Windows：没有扩展名时按固定顺序 `.exe`、`.com`、`.cmd`、`.bat`、不带扩展名尝试（不读取 `PATHEXT`）；
   `.cmd` 与 `.bat` 经 `cmd.exe /d /s /c` 启动，其他程序直接启动。
4. Linux / macOS：要求执行权限，经 `/bin/sh -c "exec '<程序>' <其余部分>"` 启动——Shell 只解析参数，随后被程序替换。
5. 找不到时直接报错 `Program not found: <名称>`，不交给系统回退搜索。

### 自定义 Agent 与 SSH 配置

自定义配置只能在界面中添加，没有对应的 CLI 子命令：

1. F9 → `Agent: Save custom CLI / SSH profile`。
2. 依次输入显示名称、可执行文件名或完整路径（默认 `ssh`）、参数（可以留空）。
3. 之后 F3 列表中就有这个配置。删除用 `Agent: Remove custom profile`，它只列出自定义配置。

只用一次的命令可以在 F3 中选择 `Custom command…` 直接输入（Windows 默认 `cmd.exe`，Linux / macOS 默认 `$SHELL` 或 `/bin/sh`），不会保存。

自定义配置保存在 `agent-profiles.json`，是一个数组：

| 字段 | 说明 |
| --- | --- |
| `Id` | 只能包含 ASCII 字母、数字、`-` 与 `_`；界面保存时生成 32 位十六进制 ID。与内置配置的 ID 相同（不区分大小写）时替换该内置配置 |
| `Name` | 显示名称，不能为空 |
| `Executable` | 程序名或完整路径，不能为空，不能含双引号或换行；界面保存时去掉两端的引号。按[发现规则](#可执行文件的查找规则)查找 |
| `Arguments` | 原样追加在程序之后的参数文本，可以为空（省略时为 `""`） |
| `IsBuiltIn` | 保存时总是 `false` |
| `CommandLine` | 写入时附带的派生字段，读取时忽略：程序加参数；程序路径含空格、制表符或 `&`、`(`、`)`、`^` 时自动加双引号 |

界面保存的 SSH 配置：

```json
[
  {
    "Id": "6f1d2c3b4a594e8d9c7b6a5f4e3d2c1b",
    "Name": "Build server",
    "Executable": "ssh",
    "Arguments": "-p 2222 dev@build01",
    "IsBuiltIn": false,
    "CommandLine": "ssh -p 2222 dev@build01"
  }
]
```

手动添加一条覆盖内置 Shell 的配置（Linux / macOS；省略的字段使用默认值）：

```json
[
  {
    "Id": "shell",
    "Name": "Bash (login)",
    "Executable": "/bin/bash",
    "Arguments": "-l"
  }
]
```

- 工作台启动时校验整个文件，任何一条无效都会使工作台无法启动，文件保持原样。
- 界面的保存与删除在跨进程锁内读取、修改、写回，不会丢掉手动添加的条目。
- SSH 会话只是一个运行 `ssh` 的终端：文件区、Git、后台任务与检查点仍然只作用于本机项目。
- Provider 按程序文件名识别。包装脚本（例如名为 `my-claude` 的脚本）不会被识别为 Claude Code，原生会话需要显式登记，
  见 [原生会话](native-sessions.md)。

## 命令模板与流水线模板

两种模板都只是“保存下来的后台任务”：在当前选中的项目目录中运行，标准输入关闭，输出保留最后 262,144 个字符，完成后记入任务历史。
需要交互或审批的程序请在终端会话中运行。每条命令最长 8,192 个字符，不能含换行等控制字符；任务、流水线与步骤名称最长 200 个字符。

### 命令模板

保存在 `workspace.json` 的 `Templates` 数组中（`CommandTemplate`）：

| 字段 | 说明 |
| --- | --- |
| `Name` | 名称，不区分大小写唯一：保存同名模板会替换旧的；运行时作为任务名称 |
| `Command` | 交给系统 Shell 的命令：Windows 为 `cmd.exe`（UTF-8 代码页），Linux / macOS 为 `/bin/sh -c` |
| `WorkingDirectory` | 保存模板时选中的项目目录，仅作记录；运行时使用当时选中的项目 |

三个字段都不能为空，否则整个 `workspace.json` 无效。格式示例见[设置](#设置)。

界面操作都在 F7 中：`Save command template`（名称默认 `Test`，命令默认 `dotnet test`）、`Run saved command`、`Remove command template`。

### 流水线模板

保存在 `pipeline-templates.json`，是一个数组（`PipelineTemplate`）：

| 字段 | 说明 |
| --- | --- |
| `Id` | 标识，不能为空；界面生成 32 位十六进制 ID |
| `Name` | 显示名称，不能为空；运行时作为任务名称 |
| `Steps` | 至少一个步骤，每个步骤有 `Name` 与 `Command`，都不能为空；界面创建时步骤名为 `Step 1`、`Step 2`…… |

```json
[
  {
    "Id": "b3a1c2d4e5f60718293a4b5c6d7e8f90",
    "Name": "Build and test",
    "Steps": [
      { "Name": "Step 1", "Command": "dotnet build -c Release" },
      { "Name": "Step 2", "Command": "dotnet test -c Release --no-build" }
    ]
  }
]
```

- 创建：F7 → `Create pipeline template`，输入名称后逐条输入命令，留空结束，界面中最多 20 步。手动编辑文件时步骤数不受这个限制，
  但一次运行的全部步骤要能放进一个不超过 131,072 字符的请求。
- 运行：F7 → `Run pipeline`。整条流水线是一个后台任务，各步骤按顺序在当前项目中执行，任一步退出码非 0 或被取消即停止；
  所有步骤的输出合并在同一个任务中，退出码为最后执行的步骤的退出码。CLI 没有运行流水线的子命令。
- 删除：F7 → `Remove pipeline template`。
- 文件在每次打开这些菜单时读取，手动修改立即生效；有无效条目时菜单报错，文件保持原样。

## 布局与项目列表

### 项目列表

`projects.json` 是一个数组，按登记顺序排列（`ProjectEntry`）：

```json
[
  {
    "Id": "5d1c0c3f8a4b4e5f9b0a1c2d3e4f5a6b",
    "Name": "my-app",
    "Path": "D:\\work\\my-app"
  }
]
```

| 字段 | 说明 |
| --- | --- |
| `Id` | 项目 ID，不能为空；布局按它索引 |
| `Name` | 显示名称，默认为目录名 |
| `Path` | 规范化后的绝对路径（不带末尾分隔符），不能为空。同一路径只登记一次，Windows 上不区分大小写 |

以下操作会登记项目：F2、`--project`、F6 中打开或创建 worktree、选中或恢复一个工作目录尚未登记的会话。`Project: Remove from sidebar`
只从列表移除，不删除目录。目录被删除或移走后条目仍然保留，文件区显示 `unavailable`。

### 布局

每个项目的标签页与分屏保存在 `workspace.json` 的 `ProjectLayouts` 中，键是项目 ID，没有选中项目时使用键 `_none`。值的结构：

| 对象 | 字段 |
| --- | --- |
| 布局（`TerminalLayout`） | `Tabs`：标签页数组，按显示顺序；`ActiveTabId`：选中的标签页，为 `null` 时为第一个 |
| 标签页（`TerminalTab`） | `Id`、`Name`、`Root`（分屏树的根）、`ActivePaneId`（选中该标签页时获得焦点的面板） |
| 节点（`LayoutNode`） | `Id`、`SessionId`、`Axis`、`Ratio`、`First`、`Second` |

- **面板**：`Axis` 为 `null`，`First` 与 `Second` 为 `null`；`SessionId` 是显示的会话 ID，空面板为 `null`。
- **分割节点**：`Axis` 为 `0`（左右排列，比例作用于宽度）或 `1`（上下排列，比例作用于高度）；`Ratio` 是分给 `First` 的比例，
  为 0.1–0.9 之间的数；`SessionId` 必须为 `null`，两个子节点都必须存在。分隔线占 1 个单元格，其余长度乘以比例后向下取整分给 `First`。
- 标签页与所有节点的 `Id` 在整个布局内互不重复；每个项目最多 16 个标签页，每个标签页最多 16 个面板，树深度不超过 16；
  标签页名称为 1–60 个非控制字符；`ActivePaneId` 必须是本标签页的面板，`ActiveTabId` 必须存在。

一个左右分屏的标签页（其余字段省略）：

```json
{
  "ProjectLayouts": {
    "5d1c0c3f8a4b4e5f9b0a1c2d3e4f5a6b": {
      "Tabs": [
        {
          "Id": "3b9f0c1d2e4a",
          "Name": "Terminal",
          "Root": {
            "Id": "7c2e5a9b1f30",
            "SessionId": null,
            "Axis": 0,
            "Ratio": 0.6,
            "First": {
              "Id": "a41d8e6c0b27",
              "SessionId": "a1b2c3d4e5f6",
              "Axis": null,
              "Ratio": 0.5,
              "First": null,
              "Second": null
            },
            "Second": {
              "Id": "e09b3f7a5c18",
              "SessionId": "0f9e8d7c6b5a",
              "Axis": null,
              "Ratio": 0.5,
              "First": null,
              "Second": null
            }
          },
          "ActivePaneId": "a41d8e6c0b27"
        }
      ],
      "ActiveTabId": "3b9f0c1d2e4a"
    }
  }
}
```

- 布局在正常离开工作台（F10 或 `Workspace: Stop all sessions and background tasks`）时写入；修改并发数或命令模板时也会写入整个
  `workspace.json`。工作台进程被强制结束时，上次写入之后的布局变化会丢失。
- 关闭面板或标签页只改变布局，会话继续在后台服务中运行。
- 布局中的会话 ID 只对当前的后台服务有意义；从侧边栏移除项目不会删除它在 `ProjectLayouts` 中的布局。

## 原子写入与冲突

### 写入流程

全部 JSON 状态文件（含检查点与 `provider-hooks/`）、源码保存与检查点恢复都经过同一个写入流程（`AtomicFile.WriteAllBytes`）：

1. 在目标文件所在目录中创建临时文件 `.crewterm-<32 位十六进制>.tmp`，以独占方式打开。
2. 写入完整内容并刷到磁盘（WriteThrough + Flush）。
3. 可选的提交前检查：源码保存与检查点恢复在这里再次比对磁盘上的内容。
4. 用重命名原子替换目标文件。目标被其他进程短暂占用（Windows 的共享冲突、锁冲突或拒绝访问）时最多尝试 12 次，
   第 n 次失败后等待 20 × n 毫秒，总计约 1.3 秒。
5. 任何一步失败都删除临时文件，目标文件保持原样。

读取时以允许其他进程同时读写和删除的方式打开文件并一次读完；读取与替换恰好重叠时，由替换方重试完成。

### 损坏的文件

读到不是合法 JSON、内容为 `null` 或字段校验失败的状态文件时，CrewTerm 报错并保留原文件，绝不用空数据覆盖。错误信息包含文件路径，
例如 `Cannot read state file; original file preserved: <路径>`。

| 损坏的文件 | 表现 |
| --- | --- |
| `projects.json`、`agent-profiles.json` | 工作台无法启动（`doctor` 读取 `agent-profiles.json`，同样报错） |
| `workspace.json` | 工作台与后台服务都无法启动；后台服务未运行时，客户端先报告后台服务启动失败 |
| `pipeline-templates.json` | 打开流水线相关菜单时报错，其他功能正常 |
| `task-history.json`、`native-sessions.json`、`agent-activity.json`、`local-plugins.json` | 后台服务无法启动，原因写入 `server-error.log` |
| `saved-sessions.json` | 只在需要迁移（`native-sessions.json` 不存在）时使后台服务无法启动；其他时候会被直接覆盖 |
| `checkpoints/` 中的元数据 / 快照 | 列出该项目的检查点时报错 / 预览该检查点时报错 |

修复方法：按本文的格式改正文件；或者把它改名移走，CrewTerm 会把不存在的文件当作空数据，代价是失去其中的内容（见上文“删除的后果”）。
修改后台服务的文件之前先停止后台服务。

### 多个客户端同时保存

同一个状态目录可以同时连接多个工作台与 CLI 命令。各文件的并发策略：

| 文件 | 策略 | 冲突时 |
| --- | --- | --- |
| `projects.json`、`workspace.json` | 乐观并发：工作台启动时记下文件内容的 SHA-256，保存时文件已被其他客户端改过就拒绝写入 | 报 `Settings changed in another client. Reload before saving: <路径>` |
| `agent-profiles.json`、`pipeline-templates.json`、`task-history.json`、`native-sessions.json`、`local-plugins.json` | 悲观并发：在以文件路径命名的跨进程互斥锁内“读取 → 修改 → 写回” | 各方的修改合并，不会互相覆盖 |
| `agent-activity.json`、`saved-sessions.json` | 单写者：只由持有 `server.lock` 的后台服务写入 | 不会发生 |

- 等待互斥锁超过 5 秒时报 `Settings are busy in another client`。
- 工作台没有“重新加载设置”的命令：遇到乐观并发冲突时，关闭并重新打开工作台即可读到最新内容。两个工作台同时打开时，
  后离开的那个在保存布局时会遇到冲突，弹出 `Layout could not be saved`，可以选择不保存布局直接离开；源码文件的保存不受影响。
- 源码文件的保存在提交前比对磁盘内容，文件已被 Agent 或其他程序修改时报
  `File changed outside the editor: <路径>. Reload or compare before saving.`，不会覆盖；检查点恢复逐个文件做同样的检查。
  这两种写入的临时文件位于被写入文件的同一目录中，见 [使用手册](user-guide.md)。

## 日常维护

### 备份与恢复

完整备份前先停止后台服务，因为它随时可能写入任务历史、会话目录与状态：

```sh
crewterm --state-dir <状态目录> stop   # 结束这套工作台的全部会话与任务
# 等后台服务进程（命令行为 crewterm --server ...）退出后，复制整个状态目录
```

`stop` 返回后，后台服务还会取消任务、记录最终结果并结束会话，然后才退出。

- **只备份配置**：`projects.json`、`workspace.json`、`agent-profiles.json`、`pipeline-templates.json` 只由工作台写入，
  关闭所有工作台后直接复制即可，不必停止后台服务。
- **可以不备份**：`server.lock`、`server.sock`、`server-error.log`、`saved-sessions.json`、`provider-hooks/` 与残留的 `.crewterm-*.tmp`。
- **检查点可能很大**：每个检查点最多 64 MiB 源文件，快照以 Base64 保存，体积约为源文件的 4/3。
- **恢复**：停止后台服务并关闭所有工作台，把备份复制回状态目录，再打开工作台。

### 迁移到其他机器

同一操作系统、项目路径也相同时，按上面的方法复制整个状态目录即可。路径或系统不同时：

| 文件 | 注意 |
| --- | --- |
| `projects.json` | `Path` 是绝对路径。路径变了就改写 `Path`，或在新机器上用 F2 重新添加；保留原来的 `Id` 才能沿用 `workspace.json` 中的布局 |
| `workspace.json` | 布局中的会话 ID 只对原来的后台服务有效；命令模板的 `WorkingDirectory` 只作记录，不影响运行 |
| `agent-profiles.json` | `Executable` 为完整路径时按新机器修改 |
| `pipeline-templates.json` | 可以直接复制 |
| `native-sessions.json` | 工作目录是绝对路径；能否恢复对话还取决于 Provider 自己的对话数据是否也在新机器上 |
| `local-plugins.json` | 不要跨操作系统复制：`SourceDirectory` 不是目标系统上的完整路径时整个登记无效，后台服务无法启动。在新机器上重新 `plugin-install` |
| `checkpoints/` | 子目录名由项目的绝对路径计算，项目换了路径后原来的检查点不会再出现在列表中 |
| `task-history.json`、`agent-activity.json` | 只是历史记录，可以不迁移 |

### 重置

| 目的 | 操作 |
| --- | --- |
| 恢复默认设置与布局 | 关闭所有工作台，删除 `workspace.json`（命令模板也一并删除） |
| 清空侧边栏 | 用 `Project: Remove from sidebar` 逐个移除，或关闭所有工作台后删除 `projects.json` |
| 清空会话目录 | 停止后台服务，删除 `native-sessions.json` 与 `saved-sessions.json`；同时删除 `agent-activity.json` 可以清空状态、通知与收件箱，`provider-hooks/` 也可以一并删除 |
| 清空任务历史 | 停止后台服务，删除 `task-history.json` |
| 完全重置 | 停止后台服务并关闭所有工作台，删除整个状态目录 |

这些操作都不会触及项目文件、插件目录或 Provider 自己的配置与对话数据。

### 多套工作台

每个状态目录是一套完全独立的工作台：自己的后台服务、会话、任务、设置、模板、历史与检查点。默认目录之外的工作台用 `--state-dir` 打开，
之后的每条命令都带上同一个目录：

```sh
crewterm --state-dir ~/crewterm-review --project ~/work/app
crewterm --state-dir ~/crewterm-review sessions
crewterm --state-dir ~/crewterm-review stop
```

- 不同工作台之间不共享会话：消息、交接与状态等待只能在同一套工作台的会话之间进行。同一个项目可以同时登记在多套工作台中。
- 会话中的回调自动回到所属的工作台（`CREWTERM_STATE_DIR`）。会话中的脚本调用其他子命令时要显式传入状态目录；
  用 `CREWTERM_EXE` 可以保证调用的是与后台服务同一版本的程序：

```sh
"$CREWTERM_EXE" --state-dir "$CREWTERM_STATE_DIR" agent-report "$CREWTERM_SESSION_ID" --state done --reason "构建通过"
```

```powershell
& $env:CREWTERM_EXE --state-dir $env:CREWTERM_STATE_DIR agent-report $env:CREWTERM_SESSION_ID --state done --reason '构建通过'
```

- 想在不停止旧版后台服务的情况下试用新版本，也可以给新版本另一个状态目录，见 [从旧后台服务切换](cli-guide.md#从旧后台服务切换)。

## 数量上限

以下数值都是源码中的常量，不能通过设置修改；例外是后台任务并发数，以及插件清单中每个命令的执行时限与保留输出。
“源码”列给出定义或检查它的文件。

### 会话与终端

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 每个后台服务的会话 | 32 个 | `DaemonHost.cs`、`DaemonHost.NativeSessions.cs` |
| 终端尺寸 | 2–300 列、2–120 行；`crewterm new` 默认 100 × 30 | `DaemonHost.cs`、`DaemonRequest.cs` |
| 会话名称 | 200 字符，不含控制字符 | `DaemonHost.cs` |
| 启动命令 | 8,192 字符，不含控制字符 | `DaemonHost.cs` |
| 滚动历史 | 2,000 行纯文本 | `TerminalScreen.cs`、`TerminalScreen.Operations.cs` |
| 一个单元格中的字形簇 | 64 个 UTF-16 单元，超出部分丢弃 | `TerminalScreen.cs` |
| 一个 CSI 序列 | 256 字符，超过时整个序列被丢弃 | `TerminalScreen.Parser.cs` |
| 粘贴 | 60,000 字符 | `WorkspaceWindow.Actions.cs` |
| 发送给 Agent 的代码上下文 | 所选内容 60,000 字符 | `CodeContext.cs` |
| 工作台的输入队列 | 2,048 条；离开工作台时最多等待 5 秒送完 | `SessionInputDispatcher.cs`、`WorkspaceWindow.cs` |
| 结束会话（Linux / macOS） | 向进程组发送 `SIGHUP`，1 秒后 `SIGKILL` | `UnixPtySession.cs` |

### 标签页与分屏

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 每个项目的标签页 | 16 个 | `TerminalLayout.cs` |
| 每个标签页的面板 | 16 个 | `TerminalLayout.cs` |
| 分屏树深度 | 16 层 | `TerminalLayout.cs` |
| 标签页名称 | 1–60 个非控制字符 | `TerminalLayout.cs` |
| 分隔线比例 | 0.1–0.9 | `TerminalLayout.cs` |

### 后台任务与流水线

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 同时执行的任务 | 1–32，默认 4（`MaximumConcurrentTasks`） | `WorkspaceSettings.cs`、`WorkspaceSettingsStore.cs` |
| 任务输出 | 最后 262,144 字符，运行中与历史记录相同 | `DaemonTaskService.cs`、`TaskRunner.cs` |
| 任务历史 | 最近 100 条（文件与后台服务内存中） | `TaskHistory.cs`、`DaemonTaskService.cs` |
| 任务命令、流水线步骤命令 | 8,192 字符，不含控制字符 | `DaemonHost.cs` |
| 任务、流水线与步骤名称 | 200 字符，不含控制字符 | `DaemonHost.cs` |
| 界面中创建流水线的步骤 | 20 步 | `WorkspaceWindow.Tools.cs` |

### Agent 状态与协作

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 消息、提示词、交接正文 | 32,768 字符 | `AgentActivityStore.Mailbox.cs`、`DaemonHost.Agents.cs` |
| 保存的通知 | 最近 200 条 | `AgentActivityStore.cs` |
| 保存的消息 | 最近 500 条 | `AgentActivityStore.cs` |
| 状态的来源 / 原因 | 64 / 4,096 字符 | `AgentActivityStore.cs` |
| 识别审批提示时检查的最近输出 | 4,096 字符 | `AgentActivityStore.cs` |
| 同时进行的状态等待 | 8 个，超出时立即失败 | `DaemonHost.cs`、`DaemonHost.Agents.cs` |
| 状态等待的超时 | 大于 0、最多 60,000 毫秒；CLI 默认 10,000 毫秒 | `AgentActivityStore.cs`、`CliCommands.cs` |
| Provider 回调载荷 | 65,536 字符；`agent-hook` 整体限时 2.5 秒 | `AgentHookCli.cs` |
| 原生会话标识 | Claude Code、Codex、Gemini、Copilot 为 36 字符的 UUID；其他为 1–256 字符（OpenCode 以 `ses_` 开头） | `ProviderSessionService.cs` |
| 检查的 Codex 配置文件 | 1 MiB，更大时不接入回调 | `AgentHookLaunchService.cs` |

### 本地插件

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 清单文件 | 128 KiB | `LocalPluginCatalog.cs` |
| 命令 / 事件钩子 | 1–64 个 / 最多 64 个 | `LocalPluginCatalog.cs` |
| 每个命令的参数 | 128 个 | `LocalPluginCatalog.cs` |
| 插件 ID、命令 ID 与事件名 | 80 字符，字母或数字开头，只含字母、数字、`.`、`_`、`-` | `LocalPluginCatalog.cs` |
| 执行时限 | 1–300 秒，默认 30 秒，包括等待执行名额的时间 | `LocalPluginCommand.cs`、`LocalPluginRunner.cs` |
| 保留输出 | 1–1,048,576 字符，默认 65,536 字符 | `LocalPluginCommand.cs`、`LocalPluginCatalog.cs` |
| 标准输入 JSON | 64 KiB（UTF-8 字节） | `LocalPluginRunner.cs` |
| 同时运行的插件进程 | 4 个 | `LocalPluginRunner.cs` |
| 同时处理的 `plugin-run` / `plugin-event` 请求 | 4 个，超出时立即失败 | `DaemonHost.cs`、`DaemonHost.Plugins.cs` |
| 等待执行的自动事件 | 32 个，队列满时丢弃该事件并记入执行历史 | `DaemonPluginService.cs` |
| 执行历史 | 最近 100 条，只在后台服务内存中，停止后清空 | `DaemonPluginService.cs` |

### 文件、搜索与检查点

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 编辑的文件 | 4 MiB（4,194,304 字节） | `FileWorkspace.cs`、`DocumentBuffer.cs` |
| 全文搜索结果 | 500 条 | `FileWorkspace.cs` |
| 每个检查点的文件 | 4,000 个 | `CheckpointStore.cs` |
| 每个检查点的大小 | 共 64 MiB，单个文件 4 MiB | `CheckpointStore.cs` |
| 恢复预览 | 每个文件显示前 8,192 字节；预览文本超过 200,000 字符后，其余文件只列出类型与路径 | `CheckpointStore.cs`、`WorkspaceWindow.Tools.cs` |
| Git 命令 | 60 秒超时 | `GitService.cs` |

### 后台服务与连接

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 请求 | 131,072 字符 | `PipeProtocol.cs` |
| 响应 | 8,388,608 字符（8 Mi） | `PipeProtocol.cs` |
| 并发连接 | 16 个 | `DaemonHost.cs` |
| 单个请求的时限 | 15 秒；`agent-wait` 为等待时间加 2 秒（客户端加 3 秒）；插件请求 305 秒（客户端 310 秒） | `DaemonHost.cs`、`DaemonClient.cs` |
| 连接等待 | 500 毫秒 | `DaemonClient.cs` |
| 等待后台服务启动 | 30 次 × 100 毫秒 | `DaemonLauncher.cs` |
| Unix 域套接字路径 | 100 字节（UTF-8），超过时改用系统临时目录 | `PipeProtocol.cs` |

### 状态文件

| 项目 | 上限 | 源码 |
| --- | --- | --- |
| 原子替换的重试 | 12 次，第 n 次失败后等待 20 × n 毫秒（约 1.3 秒） | `AtomicFile.cs` |
| 等待 JSON 文件锁 | 5 秒 | `AtomicJsonStore.cs` |
| 项目、自定义 Agent 配置、模板、会话目录记录、检查点的数量 | 没有上限 | — |
