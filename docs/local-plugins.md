# 本地插件开发指南

本文面向要为 CrewTerm 编写、调试本地插件的开发者，说明插件的能力边界、生命周期、清单格式、运行环境、事件载荷、上限，
并给出 PowerShell、bash、Python 三个可以直接使用的完整示例。CLI 的通用用法见 [CLI 使用说明](cli-guide.md)，
仓库随附的示例见 [Workspace Notes](../examples/plugins/workspace-notes/README.md)。

## 目录

- [概念与定位](#概念与定位)
- [生命周期](#生命周期)
- [清单参考](#清单参考)
- [运行环境](#运行环境)
- [事件](#事件)
- [命令调用](#命令调用)
- [结果与历史](#结果与历史)
- [上限](#上限)
- [完整示例](#完整示例)
- [调试与排错](#调试与排错)
- [安全须知](#安全须知)

## 概念与定位

本地插件是用户机器上的一个目录，目录根部的 `crewterm-plugin.json`（清单，manifest）声明若干**命令**（要启动的本地程序及其参数）
和若干**事件钩子**（某个事件发生时运行哪条命令）。CrewTerm 只登记目录路径和清单快照；命令由后台服务（daemon）以普通子进程启动，
通过标准输入传入一个 JSON 信封，收集标准输出与标准错误作为结果。

插件可以：

- 声明 1–64 条命令，在命令面板（F9 或 Ctrl+Shift+P）中以 `Plugin: 插件名 / 命令名` 运行，或用 `crewterm plugin-run` 运行。
- 用事件钩子订阅后台服务的四个自动事件：会话创建、会话退出、语义状态变化、收件箱收到消息；
  也可以订阅只由 `crewterm plugin-event` 手动触发的自定义事件名。
- 从标准输入读取 JSON 信封，得到当前项目目录、会话 ID、状态目录或事件详情。
- 以当前用户身份运行任何本地程序：读写文件、调用 `git`，也可以像普通程序一样调用 `crewterm` CLI。
- 把文本写到标准输出或标准错误，作为结果显示给用户并保存在执行历史中。

CrewTerm 0.3.0 没有以下机制：

- **没有沙箱**：插件进程拥有与你相同的文件、网络和进程权限。超时与输出上限只是资源边界，不是安全隔离。
- **没有插件面板、自定义界面、快捷键或设置项**：插件只出现在命令面板中，输出只在只读的文本查看器中显示。
- **没有插件市场、远程安装、自动更新或签名校验**：只能登记本机上已有的目录，安装过程不下载任何内容。
- **不能拦截或改变 CrewTerm 的行为**：事件在发生之后异步通知插件，插件无法阻止、修改或延迟会话操作；
  插件的输出只被保存和显示，不会被 CrewTerm 解析或执行。插件 API 本身不提供控制会话的能力。
- **不能接收终端输出流**：插件只能拿到事件载荷中的信息；需要屏幕内容时可以自行调用 `crewterm read`。
- **没有定时触发**：插件只在被调用或事件发生时运行。
- **清单不热加载**：修改清单后需要移除并重新安装；工作目录、超时与输出上限不能按平台分别设置。

## 生命周期

插件登记在状态目录的 `local-plugins.json` 中，每个状态目录有自己的一套登记和后台服务。CLI 与界面都通过后台服务操作插件；
CLI 命令需要使用与目标后台服务相同的 `--state-dir`。

| 步骤 | CLI | 命令面板 |
| --- | --- | --- |
| 安装 | `crewterm plugin-install --path DIR`（可加 `--enabled true`） | `Plugins: Install local folder`（总是安装为停用） |
| 查看 | `crewterm plugins` | `Plugins: Inspect local manifest`（来源、启用状态与清单快照） |
| 启用 | `crewterm plugin-enable ID --enabled true` | `Plugins: Enable / disable`（列表显示 ON/OFF，选中即切换） |
| 运行命令 | `crewterm plugin-run ID --command CMD [--json JSON]` | `Plugin: 插件名 / 命令名` |
| 运行钩子 | 自动事件；或 `crewterm plugin-event --event NAME [--json JSON]` | 无（自动事件照常触发） |
| 历史 | `crewterm plugin-history` | `Plugins: Execution history` |
| 停用 | `crewterm plugin-enable ID --enabled false` | `Plugins: Enable / disable` |
| 移除 | `crewterm plugin-remove ID` | `Plugins: Remove registration` |

插件 ID 可以写在子命令之后，也可以用 `--id ID`；ID 比较不区分大小写。没有单独的 `plugin-disable` 命令。用仓库示例走一遍：

```sh
crewterm plugin-install --path ./examples/plugins/workspace-notes
crewterm plugin-enable crewterm.workspace-notes --enabled true
crewterm plugin-run crewterm.workspace-notes --command summary --json '{"workingDirectory":"/path/to/project"}'
crewterm plugin-event --event session.created --json '{"sessionId":"example-session"}'
crewterm plugin-history
crewterm plugin-enable crewterm.workspace-notes --enabled false
crewterm plugin-remove crewterm.workspace-notes
```

在 Windows PowerShell 5.1 中传 `--json` 需要注意引号，见 [调试与排错](#调试与排错)。各步骤的规则：

- **安装**：读取并校验清单，记录源目录的绝对路径和清单快照；不复制、不移动文件，不执行任何插件代码，也不检查程序是否存在。
  CLI 的相对 `--path` 按调用方当前目录转换为绝对路径。插件 ID 与已登记插件重复（不区分大小写）时拒绝。默认安装为停用。
- **启用 / 停用**：每次运行前都会重新读取登记，启用或停用立即生效，不需要重启后台服务。停用只阻止新的运行，
  不会结束已经开始的进程。
- **运行**：命令按需运行；钩子在事件发生时运行。两者都只对已启用插件生效，结果都进入执行历史。
- **更新**：清单只在安装时读取一次。修改清单后要移除并重新安装，重新安装后插件又处于停用状态。
  脚本和程序仍在原目录执行，修改它们会在下一次运行时立即生效。源目录移动后也需要移除并重新安装。
- **移除**：只删除登记，源目录及插件写出的文件全部保留。历史中已有的结果保留到后台服务重启。

## 清单参考

### 文件与解析

- 文件名固定为 `crewterm-plugin.json`，位于插件目录根部，大小不超过 131072 字节（128 KiB）。使用 UTF-8 编码。
- 必须是标准 JSON：不允许注释和尾随逗号。属性名不区分大小写（`timeoutSeconds` 与 `TimeoutSeconds` 等价）。
- 出现任何未声明的属性（包括 `$schema`、拼错的字段名、平台对象中除 `executable` / `arguments` 以外的字段）、
  类型不符（例如把数字写成字符串）或语法错误时，安装失败并统一报 `Invalid plugin manifest JSON.`，不显示具体位置。

### 顶层字段

| 字段 | 类型 | 必需 | 默认值 | 规则 |
| --- | --- | --- | --- | --- |
| `id` | 字符串 | 是 | — | 符合[标识规则](#标识规则)；与已登记插件不区分大小写地唯一 |
| `name` | 字符串 | 是 | — | 非空白，不含控制字符；显示名称 |
| `version` | 字符串 | 是 | — | 非空白，不含控制字符；只用于显示，不参与比较 |
| `commands` | 命令数组 | 是 | — | 1–64 条，元素不能为 `null` |
| `hooks` | 钩子数组或 `null` | 否 | `null`（没有钩子） | 至多 64 条，元素不能为 `null` |

### 命令字段

| 字段 | 类型 | 必需 | 默认值 | 规则 |
| --- | --- | --- | --- | --- |
| `id` | 字符串 | 是 | — | 符合标识规则；同一清单内不区分大小写地唯一 |
| `name` | 字符串 | 是 | — | 非空白，不含控制字符；命令面板显示 `Plugin: 插件名 / 命令名` |
| `executable` | 字符串 | 是 | — | 非空白；不能含双引号、回车、换行或 NUL；写法见[程序查找](#程序查找) |
| `arguments` | 字符串数组或 `null` | 否 | `null`（不传参数） | 至多 128 个，元素不能为 `null` 或含 NUL；按字面传递 |
| `workingDirectory` | 字符串或 `null` | 否 | `null`（插件目录） | 相对插件目录的路径；不能带根，解析后不能逃出插件目录 |
| `timeoutSeconds` | 整数 | 否 | `30` | 1–300；包括等待执行名额的时间 |
| `maxOutputCharacters` | 整数 | 否 | `65536` | 1–1048576；保留的合并输出字符数，超出时只保留末尾 |
| `windows` | 平台对象或 `null` | 否 | `null` | 在 Windows 上替换调用 |
| `linux` | 平台对象或 `null` | 否 | `null` | 在 Linux 上替换调用 |
| `osx` | 平台对象或 `null` | 否 | `null` | 在 macOS 上替换调用（字段名是 `osx`，不是 `macos`） |

### 平台替代调用

平台对象（`LocalPluginPlatformCommand`）只有两个字段，其余字段写在命令上、三个平台共用：

| 字段 | 类型 | 默认值 | 规则 |
| --- | --- | --- | --- |
| `executable` | 字符串或 `null` | `null`（沿用命令的 `executable`） | 与命令的 `executable` 相同 |
| `arguments` | 字符串数组或 `null` | `null`（沿用命令的 `arguments`） | 与命令的 `arguments` 相同 |

选择规则：运行时按当前操作系统取 `windows`、`linux` 或 `osx` 对象；对象中 `executable` 不为 `null` 就替换程序，
`arguments` 不为 `null` 就替换整个参数列表（写 `[]` 表示不传任何参数），否则沿用默认值。没有对应对象时使用默认调用。
安装时三个平台对象都会按与默认调用相同的规则校验，而不只校验当前平台；运行前还会按当前平台的路径规则再次解析并检查。

### 事件钩子

| 字段 | 类型 | 必需 | 规则 |
| --- | --- | --- | --- |
| `event` | 字符串 | 是 | 符合标识规则；与事件名按区分大小写的方式精确比较 |
| `commandId` | 字符串 | 是 | 必须是同一清单中已声明的命令 ID（不区分大小写） |

同一个事件可以绑定多条命令，同一条命令也可以绑定多个事件；事件发生时，每个匹配的钩子各运行一次。

### 标识规则

插件 ID、命令 ID 和事件名都必须：非空、最长 80 个字符、以 ASCII 字母或数字开头，其后只含 ASCII 字母、数字、`.`、`_`、`-`
（正则 `^[A-Za-z0-9][A-Za-z0-9_.-]*\z`）。例如 `crewterm.workspace-notes`、`record-event`、`session.created`。

### 校验错误

安装失败时 CLI 在标准错误输出 `CrewTerm: 错误信息` 并返回 1，界面以标题为 “CrewTerm” 的消息框显示同样的信息；不会登记任何内容。

| 错误信息 | 原因 |
| --- | --- |
| 只有目录路径 | 源目录不存在 |
| 系统的“找不到文件”错误（含清单路径） | 目录中没有 `crewterm-plugin.json` |
| `Plugin manifest exceeds 128 KiB.` | 清单大于 131072 字节 |
| `Invalid plugin manifest JSON.` | 语法错误、注释、尾随逗号、未知属性或类型不符 |
| `Plugin manifest is null.` | 清单内容是 `null` |
| `Plugin ID must use letters, digits, dots, underscores or hyphens.` | 插件 ID 缺失或不符合标识规则 |
| `Command ID must use letters, digits, dots, underscores or hyphens.` | 命令 ID 缺失或不符合标识规则 |
| `Event name must use letters, digits, dots, underscores or hyphens.` | 钩子事件名缺失或不符合标识规则 |
| `Plugin name and version are required.` | `name` 或 `version` 缺失或为空白 |
| `Plugin and command display names must not contain control characters.` | 名称或版本含控制字符 |
| `Declare between 1 and 64 plugin commands.` | `commands` 缺失、为空或超过 64 条 |
| `Plugin command cannot be null.` / `Event hook cannot be null.` | 数组中有 `null` 元素 |
| `Duplicate plugin command ID.` | 命令 ID 重复（不区分大小写） |
| `A command needs a name.` | 命令缺少 `name` |
| `Command timeout must be 1–300 seconds and retained output 1–1048576 characters.` | 超时或输出上限越界 |
| `A command needs an unquoted executable.` | `executable` 缺失、为空白或含双引号、回车、换行、NUL |
| `Arguments must be at most 128 literal strings without NUL.` | 参数超过 128 个、有 `null` 元素或含 NUL |
| `Plugin working directories and relative program paths must stay inside the plugin directory.` | `workingDirectory` 是带根的路径 |
| `Plugin relative path escapes its source directory.` | 工作目录或相对程序路径经 `..` 等解析后逃出插件目录 |
| `At most 64 event hooks are supported per plugin.` | 钩子超过 64 条 |
| `Event hook refers to an undeclared command.` | `commandId` 缺失或不是已声明的命令 |
| `Plugin ID is already installed: ID` | 已登记相同 ID 的插件 |

### 清单完整示例

下面的清单用到了全部字段，程序 `release-helper` 只作示意：

```json
{
  "id": "acme.release-helper",
  "name": "Release Helper",
  "version": "2.1.0",
  "commands": [
    {
      "id": "draft-notes",
      "name": "Draft release notes",
      "executable": "./bin/release-helper",
      "arguments": ["draft", "--format", "markdown"],
      "workingDirectory": "data",
      "timeoutSeconds": 120,
      "maxOutputCharacters": 262144,
      "windows": { "executable": "./bin/release-helper.exe" },
      "linux": { "arguments": ["draft", "--format", "markdown", "--color", "never"] },
      "osx": { "executable": "/usr/local/bin/release-helper", "arguments": ["draft", "--format", "markdown"] }
    },
    {
      "id": "on-exit",
      "name": "Record session exit",
      "executable": "./bin/release-helper",
      "arguments": ["record-exit"],
      "windows": { "executable": "./bin/release-helper.exe" }
    }
  ],
  "hooks": [
    { "event": "session.exited", "commandId": "on-exit" },
    { "event": "acme.release-ready", "commandId": "draft-notes" }
  ]
}
```

`draft-notes` 在各平台上实际的调用（工作目录都是 `插件目录/data`，超时 120 秒，保留 262,144 个字符）：

| 平台 | 程序 | 参数 |
| --- | --- | --- |
| Windows | `插件目录\bin\release-helper.exe` | `draft --format markdown` |
| Linux | `插件目录/bin/release-helper` | `draft --format markdown --color never` |
| macOS | `/usr/local/bin/release-helper` | `draft --format markdown` |

`on-exit` 使用插件目录作为工作目录、默认超时 30 秒和默认输出上限 65,536 个字符，在每个会话退出时运行。
`acme.release-ready` 不是后台服务发出的事件，只有执行 `crewterm plugin-event --event acme.release-ready` 时才会运行 `draft-notes`。

## 运行环境

一次运行的流程：重新读取登记 → 检查插件已登记、已启用、命令已声明 → 构造标准输入信封并检查大小 → 开始计时并等待执行名额 →
解析程序与工作目录、启动进程 → 写入信封后关闭标准输入 → 等待进程退出并同时收集输出 → 生成结果并记入历史。

### 进程与身份

插件进程由后台服务启动，而不是由执行 CLI 的终端或工作台界面启动，以后台服务的用户身份运行。进程没有终端（不是伪终端，
Windows 上也不创建控制台窗口），标准输入、输出、错误都是管道，不能进行交互式输入。

### 工作目录

- 默认是插件源目录；`workingDirectory` 是相对插件源目录的子目录，每次运行前重新解析并检查不逃出插件目录。
- 目录检查只比较路径文本，不解析符号链接或目录联接；插件目录内指向外部的链接不会被发现。
- `arguments` 中的相对路径不会被 CrewTerm 解析，而是由被启动的程序按工作目录理解。设置了 `workingDirectory` 时，
  脚本路径要相对该目录书写（例如 `../run.py`）。

### 程序查找

| 写法 | 例子 | 解析方式 |
| --- | --- | --- |
| 裸程序名（不含 `/` 或 `\`） | `pwsh`、`python3`、`git` | 原样交给操作系统按 PATH 查找；不会在插件目录、项目目录或 `workingDirectory` 中查找 |
| 绝对路径 | `/usr/bin/python3`、`C:\Tools\tool.exe` | 原样使用，可以位于插件目录之外 |
| 含分隔符的相对路径 | `./run.sh`、`bin/tool`、`./tool.exe` | 相对插件源目录（不是 `workingDirectory`）解析为绝对路径，不能逃出插件目录 |

要运行插件目录中的文件，必须写成含分隔符的相对路径，例如 `./run.sh`；只写 `run.sh` 会被当作 PATH 中的程序名。

这与终端会话和后台任务的命令查找不同：那里的裸程序名由 CrewTerm 自己在 PATH 的绝对路径条目中查找，
Windows 上会依次尝试 `.exe`、`.com`、`.cmd`、`.bat` 并用 `cmd.exe` 启动 `.cmd` / `.bat`。插件不经过这套逻辑：

- Windows 上没有扩展名的名字只会补全为 `.exe`，不按 PATHEXT 查找。npm 安装的 `.cmd` 启动脚本等需要写出完整文件名，
  并且最好显式经 `cmd.exe` 启动（见[跨平台注意事项](#跨平台注意事项)）。
- Windows 的进程创建会先查找系统目录再查找 PATH，例如 `bash` 可能解析为 `C:\Windows\System32\bash.exe`（WSL），
  而不是 PATH 中的 Git Bash。需要特定程序时写绝对路径。
- Linux / macOS 上的程序必须有执行权限；脚本文件被直接执行时还需要正确的 shebang 行。

### 参数与 Shell

参数逐个通过 `ProcessStartInfo.ArgumentList` 原样传递，不经过 Shell，不做变量替换、通配符展开或拼接：参数中的 `&`、`|`、`$VAR`、
`%VAR%`、`$(...)`、空格和引号都会原样到达程序。需要 Shell 功能的插件必须把 Shell 显式声明为 `executable`，
例如 `"executable": "bash", "arguments": ["-c", "..."]`，此时由该 Shell 负责解释。

### 环境变量

插件进程继承后台服务进程的全部环境变量，CrewTerm 不添加、不删除任何变量。终端会话中的 `CREWTERM_SESSION_ID`、
`CREWTERM_STATE_DIR` 等变量不会提供给插件，需要的信息请从标准输入的载荷中读取。后台服务通常由第一个需要它的 CLI 命令或界面自动启动，
继承的是那个进程当时的环境；之后修改 PATH 等变量，要在后台服务重启后才对插件生效。

### 标准输入

标准输入是一个 JSON 对象（信封），以无 BOM 的 UTF-8 写入，写完立即关闭：

```json
{
  "schemaVersion": 1,
  "kind": "command",
  "eventName": null,
  "pluginId": "crewterm.workspace-notes",
  "commandId": "summary",
  "payload": { "workingDirectory": "/path/to/project" }
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 整数 | 固定为 `1`；插件应拒绝不认识的版本 |
| `kind` | 字符串 | 运行命令时为 `command`，事件钩子为 `event` |
| `eventName` | 字符串或 `null` | 事件名；`kind` 为 `command` 时为 `null` |
| `pluginId` | 字符串 | 清单中的插件 ID（清单原样的大小写） |
| `commandId` | 字符串 | 清单中的命令 ID（清单原样的大小写） |
| `payload` | 任意 JSON | 调用方提供的数据；没有提供时为 `null`。内容见[事件](#事件)与[命令调用](#命令调用) |

- 实际写入的是不含空白和换行的单行 JSON（本文为了可读性做了格式化），末尾没有换行符。
- 非 ASCII 字符和部分符号可能以 `\uXXXX` 转义形式出现，请用 JSON 解析器读取，不要按文本匹配。
- 整个信封按 UTF-8 计不能超过 65536 字节，否则不启动进程，结果为 `Plugin stdin JSON is limited to 64 KiB.`。
- 插件应读完标准输入，即使不需要其中的数据：信封超过管道缓冲区时，运行器会一直等待写入完成；
  如果进程仍在运行却关闭了标准输入，写入失败会被当作错误，并结束整个进程树。

### 标准输出与标准错误

- 两个流都按 UTF-8 解码，并发读取，按到达顺序合并到同一个缓冲中；结果中不区分哪一段来自哪个流。
- 只保留末尾至多 `maxOutputCharacters` 个字符。丢弃过开头部分时 `OutputTruncated` 为 `true`，输出文本本身不插入任何截断标记。
- 进程退出后，运行器会读到两个管道都关闭为止。插件启动的后台子进程若继承了这两个管道并继续运行，本次运行不会在主进程退出时结束。
- CrewTerm 不解析输出的格式；可以输出纯文本，也可以输出 JSON 供脚本读取。

### 退出码、超时与取消

- 退出码 0 表示成功；非 0 时结果失败，`Error` 为 `Plugin exited with code N`，已收集的输出照常保留。
- 超时计时从等待执行名额之前开始，排队时间也计入 `timeoutSeconds`。超时后结束进程树，最多再等待 2 秒让进程退出，
  结果 `TimedOut` 为 `true`，`Error` 为 `Plugin execution timed out.`。
- 取消发生在后台服务停止（`crewterm stop`、`Workspace: Stop all sessions and background tasks`）或请求超过后台服务的 305 秒时限时，
  结果 `Cancelled` 为 `true`，`Error` 为 `Plugin execution cancelled.`。`TimedOut` 与 `Cancelled` 不会同时为 `true`。
- 后台服务不监视客户端是否断开：关闭界面或中断 CLI 不会结束已经开始的插件运行。
- 结束进程树时按父子关系查找当时仍存活的后代进程；主进程已经退出时，运行器不会再去结束它留下的后台进程。
- 找不到程序、无法启动、非零退出、超时、取消以及其他插件错误都转换为失败结果，不会让后台服务崩溃。

### 并发与排队

| 层级 | 限制 | 名额用尽时 |
| --- | --- | --- |
| 运行器 | 同时运行至多 4 个插件进程，按需命令、手动事件和自动事件共享 | 排队等待，等待时间计入超时 |
| 后台服务请求 | 同时处理至多 4 个 `plugin-run` / `plugin-event` 请求 | 立即失败：`Maximum 4 concurrent plugin requests; retry after a plugin finishes` |
| 自动事件队列 | 容量 32 个事件，一个后台任务按顺序处理 | 新事件被丢弃，历史中记一条失败结果（见[事件](#事件)） |

自动事件逐个处理：同一事件的多个钩子并发运行，前一个事件的全部钩子结束后才处理下一个事件。因此一个慢钩子会推迟之后的所有自动事件。
同一插件的命令可以同时运行多份（例如命令面板运行的同时有钩子触发），写共享文件时要自行考虑并发。

## 事件

### 事件一览

| 事件名 | 何时触发 | `payload.sessionId` | `payload.detail` |
| --- | --- | --- | --- |
| `session.created` | 新建会话，或恢复 / 重新启动已记录的会话，进程启动成功之后 | 新会话 | 会话记录 |
| `session.exited` | 终端进程退出，或会话被关闭（每个会话的每次运行只触发一次） | 退出的会话 | 退出码与最终语义状态 |
| `agent.state` | 会话的语义状态观测发生变化 | 状态变化的会话 | 新的语义状态观测 |
| `message.received` | `message-send` 或 `agent-handoff` 把消息存入收件箱之后 | 收件会话 | 消息 |

事件名区分大小写，必须逐字书写。自动事件的 `payload` 都由后台服务构造，固定包含三个字段，属性名为 camelCase，值为 `null` 的字段也会写出：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `stateDirectory` | 字符串 | 后台服务的状态目录（绝对路径） |
| `sessionId` | 字符串 | 事件相关的 CrewTerm 会话 ID |
| `detail` | 对象 | 事件详情，结构见下文 |

时间字段是带时区偏移的 ISO 8601 字符串（UTC），路径使用当前操作系统的格式。

### session.created

由 `crewterm new`、在界面中新建会话、`session-resume` 和 `session-restart` 触发；后台服务启动时载入的休眠会话不会触发。
`detail` 是会话记录：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | 字符串 | CrewTerm 会话 ID |
| `name` | 字符串 | 显示名称 |
| `command` | 字符串 | 启动命令 |
| `workingDirectory` | 字符串 | 项目目录（绝对路径，不带末尾分隔符） |
| `provider` | 字符串或 `null` | Provider 标识（小写，如 `claude`、`codex`）；未识别时为 `null` |
| `nativeSessionId` | 字符串或 `null` | Provider 侧的原生会话标识；尚未获得时为 `null` |
| `resumeCommand` | 字符串或 `null` | 原生恢复命令；没有时为 `null` |
| `updatedAt` | 字符串 | 记录更新时间 |

```json
{
  "schemaVersion": 1,
  "kind": "event",
  "eventName": "session.created",
  "pluginId": "example.session-journal",
  "commandId": "journal",
  "payload": {
    "stateDirectory": "/home/alice/.local/share/CrewTerm",
    "sessionId": "3f9c2a7b1d04",
    "detail": {
      "id": "3f9c2a7b1d04",
      "name": "Shell",
      "command": "bash",
      "workingDirectory": "/home/alice/src/shop",
      "provider": null,
      "nativeSessionId": null,
      "resumeCommand": null,
      "updatedAt": "2026-10-26T08:15:30.1234567+00:00"
    }
  }
}
```

### session.exited

后台服务每 100 毫秒检查一次会话进程，发现退出后触发；`crewterm close` 或界面结束会话时也会触发。同一会话的同一次运行只触发一次，
恢复或重新启动后的新进程退出时会再次触发。后台服务停止时结束的会话**不会**触发（插件服务先于会话停止）。触发前总会先发出一次 `agent.state`。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `exitCode` | 整数或 `null` | 进程退出码；未知时为 `null` |
| `activity` | 对象 | 退出后的语义状态观测，结构同 `agent.state` 的 `detail`（状态为 `Exited`，来源为 `process`） |

```json
{
  "schemaVersion": 1,
  "kind": "event",
  "eventName": "session.exited",
  "pluginId": "example.session-journal",
  "commandId": "journal",
  "payload": {
    "stateDirectory": "/home/alice/.local/share/CrewTerm",
    "sessionId": "3f9c2a7b1d04",
    "detail": {
      "exitCode": 0,
      "activity": {
        "sessionId": "3f9c2a7b1d04",
        "state": "Exited",
        "source": "process",
        "reason": "Terminal process exited with code 0",
        "changedAt": "2026-10-26T09:02:11.5551234+00:00",
        "revision": 43
      }
    }
  }
}
```

### agent.state

语义状态观测（状态、来源、原因三者之一）变化时触发；三者都与当前观测相同的重复上报不触发。`detail` 是新的观测：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `sessionId` | 字符串 | 会话 ID |
| `state` | 字符串 | `Unknown`、`Working`、`Blocked`、`Done`、`Idle`、`Exited` 之一（枚举名字符串） |
| `source` | 字符串 | 证据来源标签，见下表 |
| `reason` | 字符串 | 面向用户的原因说明 |
| `changedAt` | 字符串 | 观测产生的时间 |
| `revision` | 整数 | 全局递增的版本号，可配合 `agent-wait --after-revision` 使用 |

| 状态变化来源 | `state` | `source` |
| --- | --- | --- |
| `crewterm agent-report`（显式上报） | 上报的状态 | 默认 `api`，可用 `--source` 指定 |
| Provider hook 回调 | 由 hook 事件映射 | `hook` |
| 识别到终端中的审批提示 | `Blocked` | `screen` |
| 向已知 Provider 的会话提交以回车结尾的输入 | `Working` | `input` |
| `agent-prompt` / `agent-handoff` 投递提示词 | `Working` | `prompt` |
| 终端进程退出或会话被关闭 | `Exited` | `process` |

以下状态变化**不**触发 `agent.state`：会话启动时的初始 `Unknown`、后台服务启动时把休眠会话标为 `Exited`、
确认完成通知后从 `Done` 变为 `Idle`。语义状态的含义见 [Agent 集成指南](agent-integration.md)。

```json
{
  "schemaVersion": 1,
  "kind": "event",
  "eventName": "agent.state",
  "pluginId": "example.todo-scan",
  "commandId": "attention",
  "payload": {
    "stateDirectory": "/home/alice/.local/share/CrewTerm",
    "sessionId": "3f9c2a7b1d04",
    "detail": {
      "sessionId": "3f9c2a7b1d04",
      "state": "Done",
      "source": "api",
      "reason": "Finished work",
      "changedAt": "2026-10-26T08:47:02.0034567+00:00",
      "revision": 42
    }
  }
}
```

### message.received

`message-send` 或 `agent-handoff` 把消息存入收件箱后立即触发，`payload.sessionId` 是**收件**会话。
`agent-handoff` 在把提示词写入目标终端之前就已触发，即使随后写入失败也不会撤回。`detail` 是消息：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | 字符串 | 消息 ID（32 位十六进制） |
| `fromSessionId` | 字符串 | 发件会话 |
| `toSessionId` | 字符串 | 收件会话 |
| `text` | 字符串 | 正文，最长 32768 个字符 |
| `createdAt` | 字符串 | 发送时间 |
| `acknowledged` | 布尔 | 是否已确认；新消息为 `false` |
| `inReplyTo` | 字符串或 `null` | 所回复消息的 ID |

```json
{
  "schemaVersion": 1,
  "kind": "event",
  "eventName": "message.received",
  "pluginId": "example.event-log",
  "commandId": "log",
  "payload": {
    "stateDirectory": "/home/alice/.local/share/CrewTerm",
    "sessionId": "8d21e0c4a9b7",
    "detail": {
      "id": "0b6f4c1e9d2a4f7b8c3e5a1d2f4b6c8e",
      "fromSessionId": "3f9c2a7b1d04",
      "toSessionId": "8d21e0c4a9b7",
      "text": "Please review the change",
      "createdAt": "2026-10-26T08:50:44.7781234+00:00",
      "acknowledged": false,
      "inReplyTo": null
    }
  }
}
```

很长的消息（尤其是转义后每个字符占 6 字节的非 ASCII 文本）可能使信封超过 64 KiB，此时该钩子不会启动，历史中记录失败结果。

### 手动事件与自定义事件

`crewterm plugin-event --event NAME --json JSON` 立即运行所有已启用插件中订阅了 `NAME` 的钩子，不经过自动事件队列。
`payload` 就是 `--json` 的内容（省略时为 `null`），不会自动补上 `stateDirectory`、`detail` 等字段，所以用手动事件测试时，
插件应能容忍字段缺失。`NAME` 可以是任意符合标识规则的名字，因此插件可以定义只供脚本触发的自定义事件。
没有订阅者时请求成功，返回空的结果数组。

### 失败隔离

- 每个钩子的结果相互独立：一个插件失败、超时或无法启动，不影响同一事件的其他钩子，也不影响后续事件和后台服务本身。
- 自动事件的失败只记录在执行历史中，不会显示通知。
- 但钩子共享执行名额和事件队列：慢钩子会推迟其他插件的事件处理，队列满（32 个）时新事件被丢弃，
  历史中出现一条 `PluginId` 为 `daemon`、`CommandId` 为事件名、`Error` 为 `Plugin event queue is full; this event was not executed` 的记录。
- 手动 `plugin-event` 只有全部钩子都成功时才返回成功；否则 CLI 输出第一个失败结果的错误信息并返回 1。

## 命令调用

同一条命令从不同入口运行时，信封的 `kind` 都是 `command`、`eventName` 都是 `null`，区别只在 `payload`：

| 入口 | `payload` |
| --- | --- |
| 命令面板 `Plugin: 插件名 / 命令名` | 固定为 `{ "projectRoot", "sessionId", "stateDirectory" }`，见下 |
| `crewterm plugin-run ID --command CMD --json JSON` | 原样是 `--json` 的 JSON 值（对象、数组、字符串等都可以） |
| `crewterm plugin-run ID --command CMD`（不带 `--json`） | `null` |

命令面板的载荷：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `projectRoot` | 字符串或 `null` | 当前项目目录；没有选中项目时为 `null` |
| `sessionId` | 字符串或 `null` | 活动终端面板绑定的会话 ID；面板没有会话时为 `null` |
| `stateDirectory` | 字符串 | 工作台使用的状态目录（`--state-dir` 的原样值或默认目录） |

```json
{
  "projectRoot": "/home/alice/src/shop",
  "sessionId": "3f9c2a7b1d04",
  "stateDirectory": "/home/alice/.local/share/CrewTerm"
}
```

- 界面传的是 `projectRoot`，而按惯例 CLI 调用传 `workingDirectory`。同时支持两者的插件在两个入口都能用，
  Workspace Notes 的 `summary` 就是先读 `workingDirectory`，再回退到 `projectRoot`。
- `--json` 的内容原样传递，其中的相对路径不会被转换（这与 `--path`、`--project` 不同），请传绝对路径。
- 界面运行完成后，无论成功与否都在 `PLUGIN OUTPUT · 插件ID` 查看器中显示命令、成功与否、退出码、超时、取消、截断、错误和输出，
  显示前会去掉 ANSI 转义序列和控制字符；请求本身失败（例如并发名额用尽）时以消息框显示错误。
- 命令面板会列出已启用插件的**所有**命令，包括主要供钩子使用的命令。这类命令被直接运行时最好给出有用的结果
  （例如显示已记录的内容），而不是报错。

## 结果与历史

每次运行产生一个结果（`LocalPluginResult`）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `PluginId` | 字符串 | 插件 ID；事件触发时读取登记失败为 `catalog`，事件队列已满时为 `daemon` |
| `CommandId` | 字符串 | 命令 ID；`catalog` 与 `daemon` 记录中为事件名 |
| `Success` | 布尔 | 仅当未取消、未超时且进程以退出码 0 结束时为 `true` |
| `ExitCode` | 整数或 `null` | 进程退出码；进程未启动或未能退出时为 `null` |
| `Output` | 字符串 | 标准输出与标准错误合并后的末尾部分 |
| `Error` | 字符串或 `null` | 失败说明；成功时为 `null` |
| `TimedOut` | 布尔 | 是否因超时而结束 |
| `OutputTruncated` | 布尔 | 输出是否超过上限而丢弃了开头 |
| `Cancelled` | 布尔 | 是否被取消（与 `TimedOut` 互斥） |

CLI 输出的是后台服务响应的缩进 JSON，属性名为 PascalCase，未用到的字段为 `null`：

- `plugin-run` 成功时结果在 `PluginResult` 中；失败时 CLI 只在标准错误输出 `CrewTerm: 错误信息` 并返回 1，**不输出插件的输出**，
  需要用 `plugin-history` 查看。
- `plugin-event` 的结果在 `PluginResults` 数组中。
- `plugin-history` 在 `PluginResults` 中按记录先后（最旧的在前）返回最近 100 条结果；结果在运行结束时记录。

```json
{
  "PluginId": "example.event-log",
  "CommandId": "log",
  "Success": true,
  "ExitCode": 0,
  "Output": "recorded session.created\n",
  "Error": null,
  "TimedOut": false,
  "OutputTruncated": false,
  "Cancelled": false
}
```

执行历史只保存在后台服务的内存中：按需命令、手动事件、自动事件以及队列溢出的记录都会进入历史，超过 100 条时丢弃最旧的；
后台服务重启后历史清空，不写入状态目录。界面中的 `Plugins: Execution history` 列出 `OK` / `FAILED`、插件与命令、退出码，
选中一条显示完整结果。CLI 输出中的非 ASCII 字符可能以 `\uXXXX` 形式出现，用 `ConvertFrom-Json` 或 `jq` 解析即可。

## 上限

| 项目 | 默认值 | 上限或范围 | 可否配置 |
| --- | --- | --- | --- |
| 清单文件大小 | — | 131072 字节（128 KiB） | 否 |
| 每个插件的命令数 | — | 1–64 | 否 |
| 每个插件的钩子数 | 0 | 64 | 否 |
| 插件 ID、命令 ID、事件名长度 | — | 80 个字符 | 否 |
| 每次调用的参数个数 | 0 | 128 | 否 |
| 标准输入信封 | — | 65536 字节（UTF-8） | 否 |
| 命令超时（含排队） | 30 秒 | 1–300 秒 | `timeoutSeconds` |
| 保留输出 | 65536 个字符 | 1–1048576 个字符 | `maxOutputCharacters` |
| 超时或取消后等待进程退出 | 2 秒 | — | 否 |
| 同时运行的插件进程 | 4 | 4 | 否 |
| 同时处理的 `plugin-run` / `plugin-event` 请求 | 4 | 4 | 否 |
| 自动事件队列 | 32 个事件 | 32 | 否 |
| 执行历史 | 100 条 | 100 | 否 |
| 后台服务处理插件请求的时限 | 305 秒 | — | 否 |
| CLI / 界面等待插件请求的时限 | 310 秒 | — | 否 |
| 单个请求（含 `--json`） | — | 131072 个字符 | 否 |
| 单个响应（含 `plugin-history`） | — | 8 MiB（8388608 个字符） | 否 |

历史中的每条结果都带完整的保留输出。把 `maxOutputCharacters` 设得很大时，`plugin-history` 的响应可能超过 8 MiB 而失败，
请按实际需要设置输出上限。

## 完整示例

三个示例的思路与仓库中的 [Workspace Notes](../examples/plugins/workspace-notes/README.md) 相同：读完标准输入、
解析信封、检查 `schemaVersion`、按 `kind` 或参数分派、把结果写到标准输出、把持久数据写在插件目录里。
Workspace Notes 本身就是完整的 PowerShell 示例：`summary` 命令统计项目顶层文件（同时接受 `workingDirectory` 与 `projectRoot`），
`record-event` 钩子把四个自动事件的原始信封各存为 `events/` 下的一个文件，适合用来观察插件实际收到了什么。

| 示例 | 语言 | 命令 | 钩子 | 与 Workspace Notes 的关系 |
| --- | --- | --- | --- | --- |
| Session Journal | PowerShell | `journal` | `session.created`、`session.exited` | 相同的 `pwsh` / `windows` 替代调用与读取方式；读取 `detail` 字段而不是存原始 JSON |
| Event Log | bash | `log` | 四个自动事件 | 对应 `record-event`，但把信封追加到一个 JSON Lines 文件；直接运行时显示最近的记录 |
| TODO Scan | Python | `todos`、`attention` | `agent.state` | `todos` 对应 `summary` 的两种载荷字段；`attention` 只记录 `Blocked` / `Done` |

把每个示例的文件放进一个新目录，然后安装、启用。下文的 `crewterm` 命令使用 bash / zsh 写法。

### PowerShell：Session Journal

```text
session-journal/
├─ crewterm-plugin.json
└─ journal.ps1
```

`crewterm-plugin.json`：

```json
{
  "id": "example.session-journal",
  "name": "Session Journal",
  "version": "1.0.0",
  "commands": [
    {
      "id": "journal",
      "name": "Show session journal",
      "executable": "pwsh",
      "arguments": ["-NoProfile", "-NonInteractive", "-File", "journal.ps1"],
      "windows": {
        "executable": "powershell",
        "arguments": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "journal.ps1"]
      },
      "timeoutSeconds": 20,
      "maxOutputCharacters": 8192
    }
  ],
  "hooks": [
    { "event": "session.created", "commandId": "journal" },
    { "event": "session.exited", "commandId": "journal" }
  ]
}
```

`journal.ps1`（只含 ASCII 字符，Windows PowerShell 5.1 与 PowerShell 7 都能运行）：

```powershell
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($request.schemaVersion -ne 1) { throw 'Unsupported CrewTerm plugin envelope version.' }
$journal = Join-Path $PSScriptRoot 'journal.md'

# Run from the command palette or plugin-run: print the newest entries.
if ($request.kind -eq 'command') {
    if (Test-Path -LiteralPath $journal) {
        [Console]::Out.Write((Get-Content -LiteralPath $journal -Encoding UTF8 -Tail 40) -join "`n")
    } else {
        [Console]::Out.Write('The journal is empty.')
    }
    exit 0
}

# Run by an event hook: append one line to journal.md.
$payload = $request.payload
$detail = $payload.detail
$time = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss') + 'Z'
switch ($request.eventName) {
    'session.created' {
        $line = "- $time created $($payload.sessionId): $($detail.name) ($($detail.workingDirectory))"
    }
    'session.exited' {
        $code = if ($null -eq $detail.exitCode) { 'unknown' } else { $detail.exitCode }
        $line = "- $time exited $($payload.sessionId): code $code"
    }
    default {
        $line = "- $time $($request.eventName) $($payload.sessionId)"
    }
}
[IO.File]::AppendAllText($journal, $line + "`n", [Text.UTF8Encoding]::new($false))
[Console]::Out.Write($line)
```

```sh
crewterm plugin-install --path ./session-journal
crewterm plugin-enable example.session-journal --enabled true
crewterm plugin-event --event session.exited --json '{"sessionId":"test","detail":{"exitCode":0}}'
crewterm plugin-run example.session-journal --command journal
```

默认使用 PowerShell 7（`pwsh`，Linux / macOS 上需要安装）；`windows` 替代调用让 Windows 改用系统自带的 Windows PowerShell，
`-ExecutionPolicy Bypass` 只作用于这一个进程，不修改系统的执行策略。

### bash：Event Log

```text
event-log/
├─ crewterm-plugin.json
└─ event-log.sh
```

`crewterm-plugin.json`：

```json
{
  "id": "example.event-log",
  "name": "Event Log",
  "version": "1.0.0",
  "commands": [
    {
      "id": "log",
      "name": "Show recent events",
      "executable": "bash",
      "arguments": ["event-log.sh"],
      "windows": { "executable": "C:\\Program Files\\Git\\bin\\bash.exe" },
      "timeoutSeconds": 10
    }
  ],
  "hooks": [
    { "event": "session.created", "commandId": "log" },
    { "event": "session.exited", "commandId": "log" },
    { "event": "agent.state", "commandId": "log" },
    { "event": "message.received", "commandId": "log" }
  ]
}
```

`event-log.sh`（必须以 LF 换行保存）：

```bash
#!/usr/bin/env bash
# CrewTerm local plugin: append each event envelope to events.jsonl;
# when run as a command, show the newest entries instead.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
log="$here/events.jsonl"
envelope="$(cat)"   # always read stdin completely
event_name='"eventName":"([A-Za-z0-9._-]+)"'

if [[ $envelope == '{"schemaVersion":1,"kind":"event"'* ]]; then
  # The envelope is one line of JSON, so the log stays valid JSON Lines.
  printf '%s\n' "$envelope" >> "$log"
  if [[ $envelope =~ $event_name ]]; then
    printf 'recorded %s\n' "${BASH_REMATCH[1]}"
  fi
elif [[ -f $log ]]; then
  tail -n 20 "$log"
else
  echo "No events recorded yet."
fi
```

```sh
crewterm plugin-install --path ./event-log
crewterm plugin-enable example.event-log --enabled true
crewterm plugin-event --event session.created --json '{"sessionId":"test"}'
crewterm plugin-run example.event-log --command log
```

- 用 `bash event-log.sh` 启动脚本，脚本本身不需要执行权限；改成 `"executable": "./event-log.sh"` 时需要 `chmod +x` 和 shebang 行。
- 当前实现中信封是单行 JSON，并总以 `{"schemaVersion":1,"kind":` 开头，事件名只含标识字符，示例借此用纯文本判断事件并提取
  `eventName`（第一个匹配位于 `payload` 之前）。读取其他字段请用 `jq` 等 JSON 工具。
- Linux 与 macOS（包括 macOS 自带的 bash 3.2）可以直接使用。Windows 上的 `windows` 替代调用指向 Git for Windows 的默认安装位置，
  安装在别处时改成实际路径；不写它时 `bash` 可能解析为 WSL 的 `bash.exe`。

### Python：TODO Scan

```text
todo-scan/
├─ crewterm-plugin.json
└─ plugin.py
```

`crewterm-plugin.json`：

```json
{
  "id": "example.todo-scan",
  "name": "TODO Scan",
  "version": "1.0.0",
  "commands": [
    {
      "id": "todos",
      "name": "List TODO and FIXME comments",
      "executable": "python3",
      "arguments": ["plugin.py", "todos"],
      "windows": { "executable": "python" },
      "timeoutSeconds": 60,
      "maxOutputCharacters": 32768
    },
    {
      "id": "attention",
      "name": "Show sessions that needed attention",
      "executable": "python3",
      "arguments": ["plugin.py", "attention"],
      "windows": { "executable": "python" },
      "timeoutSeconds": 10,
      "maxOutputCharacters": 16384
    }
  ],
  "hooks": [
    { "event": "agent.state", "commandId": "attention" }
  ]
}
```

`plugin.py`（Python 3.7 及以上）：

```python
#!/usr/bin/env python3
"""CrewTerm local plugin: list TODO/FIXME comments and remember sessions that need attention."""
import datetime
import json
import os
import sys

MARKERS = ("TODO", "FIXME")
SKIP_DIRS = {".git", ".hg", ".svn", "node_modules", "bin", "obj", "dist", "build", ".venv", "__pycache__"}
MAX_FILES = 5000
MAX_FILE_BYTES = 1024 * 1024
MAX_HITS = 200
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "attention.jsonl")


def object_field(value, name):
    """Return value[name] when it is a JSON object, otherwise an empty object."""
    field = value.get(name) if isinstance(value, dict) else None
    return field if isinstance(field, dict) else {}


def todos(request):
    payload = object_field(request, "payload")
    root = payload.get("workingDirectory") or payload.get("projectRoot")
    if not root or not os.path.isdir(root):
        sys.exit("payload.workingDirectory or payload.projectRoot must name an existing directory.")
    hits, scanned, stopped = [], 0, False
    for directory, subdirectories, files in os.walk(root):
        subdirectories[:] = sorted(d for d in subdirectories if d not in SKIP_DIRS)
        for name in sorted(files):
            if scanned >= MAX_FILES or len(hits) >= MAX_HITS:
                stopped = True
                break
            path = os.path.join(directory, name)
            try:
                if os.path.getsize(path) > MAX_FILE_BYTES:
                    continue
                scanned += 1
                with open(path, encoding="utf-8") as source:
                    for number, line in enumerate(source, 1):
                        if any(marker in line for marker in MARKERS):
                            hits.append("%s:%d: %s" % (os.path.relpath(path, root), number, line.strip()[:160]))
                            if len(hits) >= MAX_HITS:
                                break
            except (OSError, UnicodeDecodeError):
                continue
        if stopped:
            break
    print("\n".join(hits) if hits else "No TODO or FIXME comments found.")
    print("-- %d matches in %d files%s" % (len(hits), scanned, " (stopped early)" if stopped else ""))


def attention(request):
    if request.get("kind") == "event":
        payload = object_field(request, "payload")
        detail = object_field(payload, "detail")
        if detail.get("state") not in ("Blocked", "Done"):
            return
        entry = {
            "at": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
            "sessionId": payload.get("sessionId"),
            "state": detail.get("state"),
            "reason": detail.get("reason"),
        }
        with open(LOG, "a", encoding="utf-8") as log:
            log.write(json.dumps(entry, ensure_ascii=False) + "\n")
        print("%s %s" % (entry["state"], entry["sessionId"]))
        return
    if not os.path.exists(LOG):
        print("No session has needed attention yet.")
        return
    with open(LOG, encoding="utf-8") as log:
        lines = log.readlines()[-30:]
    for line in lines:
        entry = json.loads(line)
        print("%s  %-7s %s  %s" % (entry["at"], entry["state"], entry["sessionId"], entry["reason"]))


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")
    request = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    if not isinstance(request, dict) or request.get("schemaVersion") != 1:
        sys.exit("Unsupported CrewTerm plugin envelope version.")
    action = sys.argv[1] if len(sys.argv) > 1 else ""
    if action == "todos":
        todos(request)
    elif action == "attention":
        attention(request)
    else:
        sys.exit("usage: plugin.py todos|attention")


if __name__ == "__main__":
    main()
```

```sh
crewterm plugin-install --path ./todo-scan
crewterm plugin-enable example.todo-scan --enabled true
crewterm plugin-run example.todo-scan --command todos --json '{"workingDirectory":"/path/to/project"}'
crewterm plugin-event --event agent.state --json '{"sessionId":"test","detail":{"state":"Done","reason":"manual test"}}'
crewterm plugin-run example.todo-scan --command attention
```

- 从命令面板运行 `todos` 时使用界面的 `projectRoot`；`attention` 在事件中记录、在直接运行时显示最近 30 条记录。
- 脚本自己按 UTF-8 读写标准输入输出，不依赖系统区域设置；`sys.exit("...")` 把信息写到标准错误并以退出码 1 结束。
- Windows 上通常没有 `python3`，替代调用改用 `python`；只装了 `py` 启动器时，把 `windows` 改为
  `{ "executable": "py", "arguments": ["-3", "plugin.py", "todos"] }`（`attention` 同理）。

### 跨平台注意事项

| 主题 | 说明 |
| --- | --- |
| 路径分隔符 | 清单中的相对路径用 `/`，Windows 也能识别；Linux / macOS 把 `\` 当作普通文件名字符 |
| 执行权限 | Linux / macOS 上直接执行的程序或脚本需要执行权限（`chmod +x`）；经解释器启动（`bash run.sh`、`python3 plugin.py`）则不需要 |
| shebang | 直接执行的脚本必须以 `#!/usr/bin/env bash` 之类的 shebang 行开头，否则启动失败 |
| 换行符 | 在 Linux / macOS 上运行的 shell 脚本必须是 LF 换行；CRLF 会使 shebang 指向 `bash\r` 之类不存在的解释器，bash 也会把 `\r` 当作命令的一部分。在 Git 仓库中可用 `.gitattributes` 固定 `*.sh text eol=lf` |
| Windows 上的 `.ps1` | 不能作为 `executable` 直接启动，要写成 `powershell` / `pwsh` 加 `-File 脚本.ps1`；Windows PowerShell 5.1 在默认执行策略下需要 `-ExecutionPolicy Bypass`（或改为允许脚本的执行策略）。含非 ASCII 字符的脚本在 5.1 中要保存为带 BOM 的 UTF-8 |
| Windows 上的 `.cmd` / `.bat` | 写成 `"executable": "cmd.exe", "arguments": ["/d", "/c", "run.cmd"]`；此时整行参数由 `cmd.exe` 解释，`&`、`\|`、`^`、`%VAR%` 都有特殊含义，只放固定的字面参数 |
| Windows 上的程序名 | 不按 PATHEXT 补全，`.exe` 以外的程序要写完整文件名；系统目录先于 PATH 查找 |
| 输出编码 | 运行器按 UTF-8 解码输出。Windows PowerShell 要设置 `[Console]::OutputEncoding`，Python 用 `reconfigure(encoding="utf-8")` 或 `-X utf8` |
| macOS 字段名 | 平台对象写 `osx` |

## 调试与排错

### 脱离 CrewTerm 运行脚本

插件就是一个读标准输入的普通程序。在插件目录中手工构造一个信封，通过管道交给脚本，可以先排除脚本本身的问题：

```sh
cd todo-scan
printf '%s' '{"schemaVersion":1,"kind":"command","eventName":null,"pluginId":"example.todo-scan","commandId":"todos","payload":{"workingDirectory":"/path/to/project"}}' \
  | python3 plugin.py todos
echo "exit code: $?"
```

需要真实的事件载荷时，可以临时启用 Workspace Notes，它会把收到的每个事件信封原样保存到它目录下的 `events/` 中。

### 查看历史与退出码

```sh
crewterm plugin-history | jq '.PluginResults[] | select(.Success | not) | {PluginId, CommandId, ExitCode, TimedOut, Cancelled, Error, Output}'
```

```powershell
(crewterm plugin-history | ConvertFrom-Json).PluginResults |
    Where-Object { -not $_.Success } |
    Format-List PluginId, CommandId, ExitCode, TimedOut, Cancelled, Error, Output
```

`plugin-run` 失败时 CLI 返回 1，标准错误只有一行错误信息，插件的输出要在历史中查看。界面中用 `Plugins: Execution history`。
自动事件的钩子失败不会弹出提示，定期检查历史，或在钩子里把错误写到自己的日志文件中。

### 常见错误

| 现象或错误信息 | 原因与处理 |
| --- | --- |
| `Plugin is disabled: ID` | 插件未启用；`crewterm plugin-enable ID --enabled true` |
| `Plugin not installed: ID` | 没有登记该 ID；用 `crewterm plugins` 检查，确认 `--state-dir` 与目标后台服务一致 |
| `Plugin command not declared: CMD` | 命令 ID 不在清单快照中；修改清单后需要移除并重新安装 |
| `Plugin exited with code N` | 插件自身报错；在历史的 `Output` 中查看它写出的错误 |
| `Plugin execution timed out.` | 超过 `timeoutSeconds`（含排队时间）；检查脚本是否在等待交互输入、是否留下了持有输出管道的后台进程，必要时提高超时（最多 300 秒） |
| `Plugin execution cancelled.` | 后台服务正在停止，或请求超过了后台服务的 305 秒时限 |
| `Plugin stdin JSON is limited to 64 KiB.` | 载荷太大，例如很长的消息正文；减小 `--json`，或接受该事件无法交给插件 |
| `An error occurred trying to start process '…' with working directory '…'. …` | 进程无法启动，末尾是操作系统给出的原因：找不到程序（PATH、扩展名、Windows 系统目录优先）、缺少执行权限或 shebang、`.ps1` 被直接执行、工作目录不存在（源目录被移动或删除） |
| `Maximum 4 concurrent plugin requests; retry after a plugin finishes` | 已有 4 个 `plugin-run` / `plugin-event` 请求在处理中；稍后重试 |
| 历史中 `PluginId` 为 `daemon`：`Plugin event queue is full; this event was not executed` | 钩子处理太慢，自动事件积压超过 32 个；缩短钩子耗时 |
| `--json must be valid JSON: …` | JSON 本身有误，或被调用方 Shell 改写。Windows PowerShell 5.1（以及 PowerShell 7.3 之前的版本）向原生程序传参时不会转义参数中的双引号，JSON 会被拆坏；请改用 PowerShell 7.3 及以上版本、bash 或 cmd |
| `Option --json requires a value` | 选项后缺少取值，或取值以 `--` 开头 |
| `Input too large for one request` | 请求超过 131072 个字符，通常是 `--json` 太大 |
| `Protocol message exceeds maximum size` | 响应超过 8 MiB，通常是历史中有大量大输出；降低 `maxOutputCharacters` |
| 输出乱码 | 插件没有以 UTF-8 写出标准输出；见跨平台注意事项中的“输出编码” |
| 命令面板中没有插件命令 | 插件未启用，或连接的是旧版后台服务（界面显示 “OLD SERVER” 时命令面板不列出插件命令） |
| 钩子没有运行 | 插件未启用；事件名大小写不符；该情形本来就不触发事件（例如后台服务停止时结束的会话、确认完成通知）；事件被队列丢弃。先查看历史 |
| 后台服务无法启动，`server-error.log` 中有 `Invalid local plugin registry; original bytes preserved.` 或 `Cannot read state file; original file preserved: …local-plugins.json` | 插件登记文件损坏或被手工改坏，CrewTerm 不会悄悄把它重置为空登记。修复该文件，或把它移走后重新安装插件（移走只丢失登记，不影响插件目录） |

CLI 自身的退出码：请求处理成功为 0；参数错误、插件运行失败、请求失败时为 1。完整的 CLI 规则见 [CLI 使用说明](cli-guide.md)，
其他问题见 [常见问题](faq.md)。

## 安全须知

- **只启用你信任并检查过的插件。** 插件进程以你的用户身份运行，没有沙箱，可以读写你能访问的任何文件、访问网络、启动其他程序，
  也可以调用 `crewterm` CLI 操作你的会话。超时和输出上限不是安全措施。
- **启用前看实际内容。** 用 `Plugins: Inspect local manifest` 或 `crewterm plugins` 确认源目录和清单快照，
  再检查清单引用的脚本与程序。清单是安装时的快照，而脚本和程序可以在之后被改动，改动在下一次运行时立即生效，不需要重新确认。
- **插件继承后台服务的环境变量**，其中可能包括 API 密钥、令牌等敏感信息。
- **路径检查只是文本检查。** 相对路径不能用 `..` 逃出插件目录，但不解析符号链接；绝对路径的程序可以位于任何位置。
- **把载荷当作不可信输入。** 消息正文、会话名称、启动命令等可能来自 Agent 或其他会话，不要拼进 Shell 命令或 `eval`；
  经 `cmd.exe` 启动 `.cmd` / `.bat` 时，参数会被 `cmd.exe` 重新解释。
- **小心循环。** 钩子中调用 `crewterm message-send`、`agent-report` 等命令会产生新的事件，可能再次触发自己。
- **停用不会结束已在运行的进程**；需要立即停止时，`crewterm stop` 会停止后台服务并取消所有插件运行（同时结束全部会话与任务）。
- **移除登记不删除任何文件。** 插件写在自己目录中的数据需要手工清理。
- 插件名称、版本中的控制字符在安装时被拒绝；界面显示插件输出前会去掉终端转义序列，插件输出无法操纵你的终端。
- CrewTerm 的整体安全模型见 [README](../README.md#安全模型)，后台服务协议中的 `plugin-*` 操作见 [后台服务协议](protocol.md)，
  状态目录中的文件见 [配置与数据参考](configuration.md)。
