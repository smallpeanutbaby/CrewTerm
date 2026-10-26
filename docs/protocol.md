# 后台服务通信协议

本文是 CrewTerm 后台服务（daemon）本机通信协议的参考：传输与访问控制、帧格式、请求与响应对象、全部请求类型与返回结构，
面向想自己编写客户端或集成工具的开发者。内容对应 CrewTerm 0.3.0（协议版本 2），取自 `src/CrewTerm.App` 中 `Protocol`、
`Server`、`Client`、`Cli` 各目录的源码。

> 编写脚本与集成时优先使用 `crewterm` 子命令（见 [CLI 使用说明](cli-guide.md)）：它们在连接前校验参数、按需启动后台服务、
> 识别旧版服务，并以 JSON 输出与本文相同的响应对象。直接使用协议适合需要高频轮询（例如读取屏幕）、
> 不想为每次调用启动一个进程的工具。

## 目录

- [1. 定位与兼容策略](#1-定位与兼容策略)
- [2. 传输](#2-传输)
- [3. 帧格式](#3-帧格式)
- [4. 请求对象](#4-请求对象)
- [5. 响应对象](#5-响应对象)
- [6. 请求类型总览](#6-请求类型总览)
- [7. 请求详解](#7-请求详解)
- [8. 返回结构](#8-返回结构)
- [9. 输入规则与错误](#9-输入规则与错误)
- [10. 最小客户端示例](#10-最小客户端示例)
- [相关文档](#相关文档)

## 1. 定位与兼容策略

协议是 CrewTerm 自己的客户端（工作台界面、CLI 与 `agent-hook` 回调）和后台服务之间的内部接口。源码中有下面这些兼容约定，
但没有为第三方客户端承诺稳定性；升级 CrewTerm 后请按新版本的源码或本文重新核对。

| 方面 | 约定 |
| --- | --- |
| 协议版本 | 一个整数 `CrewTermVersion.ProtocolVersion`，当前为 `2`（支持语义状态、原生会话与插件）。它只出现在 `ping` 响应的 `ProtocolVersion` 中；0.1 版后台服务不返回这个字段 |
| 字段名 | 读取时不区分大小写；写出时是 C# 属性名（PascalCase，例如 `SessionId`） |
| 未知字段 | 忽略 |
| 新增字段 | 必须是可选的，默认值保持旧版本的行为（例如 `IncludeText` 默认 `true`），新旧版本的客户端与后台服务因此可以互通 |
| 操作名 | 区分大小写，没有别名；不认识的操作返回失败响应 `Unknown operation: <操作名>`（以 `plugin-` 开头的为 `Unknown plugin operation: <操作名>`） |

### 1.1 启动握手

CrewTerm 的客户端连接后台服务时先发送 `ping`（`DaemonLauncher`），再用 `CrewTermVersion.ServerWarning` 检查响应：
`ProtocolVersion` 不小于 2 时正常使用；字段缺失或小于 2 时视为旧版服务，生成下面的英文警告。
其中 `<版本>` 依次取响应的 `Version`、`Text`（旧版服务为 `CrewTerm 0.1.0` 这样的文本），都没有时为 `an older release`。

```text
Connected daemon is <版本>; CrewTerm <当前版本> Agent features require a daemon restart. Existing terminals remain running. Finish or save their work before explicitly running 'crewterm stop', then reopen CrewTerm.
```

连接到旧版服务时：

| 客户端 | 行为 |
| --- | --- |
| 工作台界面 | 标题显示 `OLD SERVER · F1 for upgrade instructions`，状态栏与 F1 帮助显示警告；命令面板不列插件命令 |
| CLI | 把 `CrewTerm: <警告>` 写到标准错误，然后继续执行 |
| 两者 | 需要新协议的请求在本地直接失败（错误消息就是警告），不会发给旧服务；已有终端继续运行；从不自动发送 `stop` |

不需要新协议的操作（`CrewTermVersion.RequiresCurrentProtocol` 返回 `false`）是 `ping`、`list`、`input`、`screen`、`resize`、
`close`、`stop`、`task-start`、`pipeline-start`、`task-list`、`task-get`、`task-cancel`，以及不带 `Provider`、`NativeSessionId`、
`ResumeCommand` 的 `create`；其余操作一律需要协议版本 2。自己编写的客户端应同样先发送 `ping`，把缺失的 `ProtocolVersion`
当作旧版服务处理。升级旧服务只能由用户显式完成：保存旧终端中的工作后运行 `crewterm stop`，再重新打开 CrewTerm
（见 [从旧后台服务切换](cli-guide.md#从旧后台服务切换)）。

## 2. 传输

每个状态目录对应一个后台服务和一个端点：Windows 上是命名管道，Linux / macOS 上是 Unix 域套接字（.NET 的命名管道类型
在这两个平台上以 Unix 域套接字实现）。状态目录由 `--state-dir` 指定，默认是 .NET `LocalApplicationData` 下的 `CrewTerm`：
Windows `%LOCALAPPDATA%\CrewTerm`，Linux `$XDG_DATA_HOME/CrewTerm`（未设置时为 `~/.local/share/CrewTerm`），
macOS `~/Library/Application Support/CrewTerm`。在 CrewTerm 启动的会话中，环境变量 `CREWTERM_STATE_DIR`
是该会话所属后台服务的状态目录（绝对路径），`CREWTERM_SESSION_ID` 是会话标识。

### 2.1 端点名称

客户端与后台服务用同一个函数 `PipeProtocol.PipeName(stateDirectory)` 计算端点名称：

1. `path` = `Path.GetFullPath(stateDirectory)`，再去掉末尾的目录分隔符。
2. Windows：`key` = `SHA-256(UTF-8(path.ToUpperInvariant()))` 的十六进制大写形式的前 24 个字符；
   管道名为 `CrewTerm-<用户名>-<key>`，完整路径为 `\\.\pipe\CrewTerm-<用户名>-<key>`。
3. Linux / macOS：`socket` = `Path.Combine(path, "server.sock")`；它的 UTF-8 字节数不超过 100 时，端点就是这个绝对路径。
4. Linux / macOS 且超过 100 字节：`key` = `SHA-256(UTF-8(用户名 + "|" + path))` 的十六进制小写形式的前 24 个字符，
   名称为 `crewterm-<key>`。它不是绝对路径，.NET 把它放进临时目录：实际套接字是 `<临时目录>CoreFxPipe_crewterm-<key>`，
   临时目录取 `$TMPDIR`（补齐末尾的 `/`），未设置或为空时为 `/tmp/`。

“用户名”是 .NET 的 `Environment.UserName`：Windows 上是 `GetUserNameExW(NameSamCompatible)` 结果中第一个反斜杠之后的部分
（不含域名的登录名），Linux / macOS 上是有效用户 ID 在 passwd 中的用户名。可以用下面的数据检验自己的实现：

| 平台 | 用户名 | 状态目录 | 端点 |
| --- | --- | --- | --- |
| Windows | `me` | `C:\Users\me\AppData\Local\CrewTerm` | `\\.\pipe\CrewTerm-me-91BE557B30FFFFC0217E3F25` |
| Linux | 任意 | `/home/me/.local/share/CrewTerm` | `/home/me/.local/share/CrewTerm/server.sock`（42 字节） |
| Linux（未设置 `TMPDIR`） | `me` | `/home/me/projects/an-unusually-long-directory-name-for-testing/nested/crewterm-state-directory` | `/tmp/CoreFxPipe_crewterm-7cbcfa6715f5b497c849c7ed`（`server.sock` 路径为 106 字节，超过上限） |

要点：

- Windows 上路径不区分大小写；`GetFullPath` 统一分隔符并解析 `.` 与 `..`，但不解析目录联接与符号链接，
  经过联接或链接的另一种写法会得到另一个管道名，连不到已在运行的后台服务。
- Linux / macOS 的常规情况下，套接字位于状态目录中、与 `server.lock` 同目录：同一目录的任何写法都指向同一个文件，
  也不受各终端 `TMPDIR` 不同的影响。最后一个管道实例释放（后台服务停止）时套接字文件被删除。
- 退回临时目录时，名称取决于路径的写法与用户名，位置取决于后台服务进程启动时的 `TMPDIR`：客户端的 `TMPDIR` 必须与之相同。
- 100 字节的上限为 `sun_path`（macOS 104 字节、Linux 108 字节，含结尾的 NUL）留出余量。

### 2.2 访问控制

后台服务与 `DaemonClient` 都以 `PipeOptions.CurrentUserOnly` 打开端点，由 .NET 完成下面的校验：

| 平台 | 后台服务一侧 | 客户端一侧（校验后台服务的身份） |
| --- | --- | --- |
| Windows | 管道的安全描述符只授予当前用户访问，所有者是当前用户 | 连接后读取管道的所有者，不是当前用户则抛出 `UnauthorizedAccessException` |
| Linux / macOS | 接受连接时比较对端进程与自己的有效用户 ID，不同则拒绝 | 连接后比较后台服务进程与自己的有效用户 ID，不同则抛出 `UnauthorizedAccessException` |

此外，Linux / macOS 上新建的状态目录权限为 `0700`（`StateFiles.EnsureDirectory`），其他用户无法抢先占用其中的 `server.sock`。
身份校验失败时 CrewTerm 的客户端报错退出，不会因此另起一个后台服务。自己编写的客户端应做同样的校验：
连接后确认对端属于当前用户，再发送请求（第 10 节的示例演示了做法和各自的限制）。

### 2.3 后台服务的启动与停止

| 方面 | 规则 |
| --- | --- |
| 何时启动 | 工作台界面（`crewterm`、`crewterm ui`）与非交互子命令在参数全部校验通过后调用 `DaemonLauncher.EnsureRunningAsync`：先 `ping`，没有响应才启动。`crewterm stop` 只探测、从不启动；`crewterm agent-hook` 直接连接、从不启动；`--help`、`--version`、`doctor` 不连接 |
| 如何启动 | 运行同一个可执行文件：`crewterm --server --state-dir <绝对路径>`（以 `dotnet crewterm.dll` 运行时，参数前加上程序集路径）。进程脱离调用方：不继承调用方的标准输入输出，工作目录是程序所在目录，环境变量继承自启动它的客户端；Linux / macOS 上启动后再创建新会话，并把标准输入输出改接到 `/dev/null` |
| 就绪判断 | 启动后最多 `ping` 30 次，间隔 100 毫秒（每次连接最多等待 500 毫秒）；仍无响应时报错 `Session server did not start. Inspect server-error.log in <状态目录>` |
| 唯一实例 | 运行期间独占打开状态目录中的 `server.lock`；同一状态目录的第二个后台服务启动失败，错误追加到 `server-error.log` |
| 停止 | 只有 `stop` 请求会让后台服务正常退出：写回成功响应后停止监听，取消后台任务并等待它们记录结果，最后结束全部终端会话的进程树。工作台分离（F10）或客户端退出都不影响后台服务 |

协议本身没有“启动”请求。自己编写的客户端需要确保后台服务在运行时，最简单的做法是先执行一次
`crewterm --state-dir <状态目录> sessions`。

## 3. 帧格式

每个连接只交换一次：

1. 客户端连接端点，写入一行 JSON 请求（`DaemonRequest`），以 `\n` 结尾；写 `\r\n` 也可以，行尾的 `\r` 会被去掉。
2. 后台服务按块读取到第一个 `\n` 为止，之后的内容丢弃；执行请求后写回一行 JSON 响应（`DaemonResponse`），
   以后台服务所在平台的换行结尾（Windows `\r\n`，Linux / macOS `\n`）。
3. 后台服务关闭连接。下一个请求需要新的连接。

| 方面 | 规则 |
| --- | --- |
| 编码 | 双向 UTF-8，不带 BOM |
| 请求上限 | 131072 个字符（`PipeProtocol.MaxRequest`，128 Ki，按 .NET 的 UTF-16 字符计，不含换行），足以容纳 60,000 字符的粘贴及其 JSON 转义。超过时后台服务不回响应、直接关闭连接；`DaemonClient` 在发送前检查，抛出 `Input too large for one request` |
| 响应上限 | 8388608 个字符（`PipeProtocol.MaxResponse`，8 Mi），足以容纳最大尺寸的屏幕快照与 2,000 行滚动历史；由客户端检查，超过时抛出 `Protocol message exceeds maximum size` |
| JSON 选项 | 双方都使用 `PipeProtocol.Json`：属性名不区分大小写；非 ASCII 字符按 UTF-8 原样写出（`UnsafeRelaxedJsonEscaping`），不转成 `\uXXXX`。读取方按标准 JSON 解析，转义与否都能读 |
| 空值 | 响应总是写出全部字段，没有值的字段为 `null`（`TerminalCell` 的布尔样式字段例外，见第 8 节）。请求可以省略任何可选字段，但 `Columns`、`Rows`、`TimeoutMilliseconds`、`IncludeText` 不能写成 `null` |
| 语义状态 | 以名称字符串表示，例如 `"Working"`（`AgentActivityState` 带 `JsonStringEnumConverter`） |
| 时间 | ISO 8601 文本，带时区偏移，例如 `"2026-10-26T08:15:30.1234567+00:00"` |

### 3.1 并发与超时

| 方面 | 上限或时长 |
| --- | --- |
| 同时处理的连接 | 16 个；另有一个始终存在的监听实例（管道实例上限 17）。超出的客户端在连接阶段等待 |
| 同时进行的 `agent-wait` | 8 个；名额用尽时立即失败：`Maximum 8 concurrent Agent waits; retry after a wait completes` |
| 同时进行的 `plugin-run` 与 `plugin-event` | 合计 4 个；名额用尽时立即失败：`Maximum 4 concurrent plugin requests; retry after a plugin finishes` |
| 后台服务每个连接的期限 | 15 秒，包括读取请求、执行与写回；读到 `agent-wait` 后改为 `TimeoutMilliseconds`（限制在 1–60000）加 2 秒，读到 `plugin-run` / `plugin-event` 后改为 305 秒。到期后连接被关闭，不再写回响应 |
| `DaemonClient` 的连接等待 | 500 毫秒；连不上时抛出 `TimeoutException`（后台服务未运行或连接已满） |
| `DaemonClient` 每次调用的期限 | 15 秒；`agent-wait` 为 `TimeoutMilliseconds`（限制在 1–60000）加 3 秒，`plugin-run` / `plugin-event` 为 310 秒。客户端比后台服务多留 1 秒与 5 秒，保证能收到后台服务的失败响应 |

## 4. 请求对象

`DaemonRequest`：只有 `Operation` 必填，其余字段按操作取用，用不到的字段被忽略。各操作需要哪些字段见第 6 节。

| 字段 | JSON 类型 | 默认值 | 含义 |
| --- | --- | --- | --- |
| `Operation` | 字符串 | 必填 | 操作名，见第 6 节 |
| `SessionId` | 字符串 | `null` | 目标会话标识；任务操作中是任务标识 |
| `Command` | 字符串 | `null` | 要启动的命令行（`create`、`task-start`） |
| `WorkingDirectory` | 字符串 | `null` | 命令的工作目录，必须是绝对路径；省略时为状态目录（`create`、`task-start`） |
| `Text` | 字符串 | `null` | 原始终端输入（`input`）、提示词（`agent-prompt`、`agent-handoff`）或消息正文（`message-send`） |
| `Columns` | 整数 | `100` | 终端列数（`create`、`resize`、`session-resume`、`session-restart`），2–300 |
| `Rows` | 整数 | `30` | 终端行数（同上），2–120 |
| `Name` | 字符串 | `null` | 会话、任务或流水线的显示名称 |
| `Steps` | 对象数组 | `null` | 流水线步骤，每项为 `{"Name", "Command", "WorkingDirectory"}`（`pipeline-start`） |
| `State` | 字符串 | `null` | 单个语义状态（`agent-report`；`agent-wait` 没有 `States` 时使用） |
| `States` | 字符串数组 | `null` | 等待其中任一状态（`agent-wait`） |
| `Source` | 字符串 | `null` | 状态来源标签（`agent-report`，省略时为 `api`） |
| `Reason` | 字符串 | `null` | 状态原因（`agent-report`，省略时为 `Explicit state report`） |
| `TimeoutMilliseconds` | 整数 | `10000` | 等待超时（`agent-wait`），1–60000 |
| `AfterRevision` | 整数（64 位） | `null` | 只接受版本号大于它的状态（`agent-wait`） |
| `FromSessionId` | 字符串 | `null` | 发件会话（`message-send`、`agent-handoff`） |
| `TargetSessionId` | 字符串 | `null` | 收件会话（`message-send`、`agent-handoff`） |
| `NotificationId` | 字符串 | `null` | 通知标识（`notification-read`） |
| `MessageId` | 字符串 | `null` | 消息标识（`message-ack`） |
| `InReplyTo` | 字符串 | `null` | 所回复的消息标识（`message-send`、`agent-handoff`） |
| `Provider` | 字符串 | `null` | Provider，例如 `claude`、`codex`（`create`、`session-bind`、`agent-hook`） |
| `NativeSessionId` | 字符串 | `null` | 精确的原生会话标识（`create`、`session-bind`） |
| `ResumeCommand` | 字符串 | `null` | 显式登记的恢复命令（`create`、`session-bind`） |
| `PluginId` | 字符串 | `null` | 插件标识（`plugin-enable`、`plugin-remove`、`plugin-run`） |
| `PluginCommand` | 字符串 | `null` | 插件清单中声明的命令标识（`plugin-run`） |
| `Path` | 字符串 | `null` | 本地插件目录，必须是绝对路径（`plugin-install`） |
| `Enabled` | 布尔 | `null` | 启用状态（`plugin-install` 省略时为 `false`；`plugin-enable` 必填） |
| `EventName` | 字符串 | `null` | 插件事件名（`plugin-event`） |
| `Payload` | 任意 JSON | `null` | 传给插件的数据（`plugin-run`、`plugin-event`），或 Provider hook 负载（`agent-hook`，必须是对象） |
| `IncludeText` | 布尔 | `true` | `screen` 响应是否附带整屏纯文本；只需要单元格时传 `false`，传输量可减少一半以上 |

## 5. 响应对象

`DaemonResponse`：`Ok` 表示请求是否成功，失败时 `Error` 是原因，其余字段按操作填写。

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Ok` | 布尔 | 请求是否成功 |
| `Error` | 字符串 | 失败原因（英文）；成功时为 `null` |
| `SessionId` | 字符串 | 新建或受影响的会话、任务标识 |
| `Sessions` | `SessionInfo` 数组 | 会话列表（`list`、`session-catalog`、`session-bind`） |
| `Screen` | `TerminalSnapshot` | 屏幕快照（`screen`） |
| `Text` | 字符串 | 整屏纯文本（`screen`），或给用户的说明文字（`ping`、`session-resume`、`session-restart`、`agent-hook`） |
| `Tasks` | `DaemonTaskInfo` 数组 | 任务列表，不含输出（`task-list`） |
| `Task` | `DaemonTaskInfo` | 单个任务，含输出（`task-get`） |
| `Activity` | `AgentActivity` | 单个会话的语义状态 |
| `Activities` | `AgentActivity` 数组 | 全部会话的语义状态，按优先级降序（`agent-state`） |
| `Notifications` | `AgentNotification` 数组 | 通知列表（`notifications`） |
| `Message` | `AgentMessage` | 刚保存的消息（`message-send`、`agent-handoff`） |
| `Messages` | `AgentMessage` 数组 | 收件箱中的消息（`message-list`） |
| `Delivered` | 布尔 | 提示词是否已写入目标终端（`agent-prompt`、`agent-handoff`；`message-send` 固定为 `false`） |
| `Plugin` | `LocalPluginInfo` | 单个插件（`plugin-install`、`plugin-enable`） |
| `Plugins` | `LocalPluginInfo` 数组 | 已登记的插件（`plugin-list`） |
| `PluginResult` | `LocalPluginResult` | 一次插件命令的结果（`plugin-run`） |
| `PluginResults` | `LocalPluginResult` 数组 | 一次事件触发的全部结果（`plugin-event`），或执行历史（`plugin-history`） |
| `Version` | 字符串 | 后台服务的版本号，例如 `0.3.0`（`ping`） |
| `ProtocolVersion` | 整数 | 后台服务支持的协议版本（`ping`）；旧版服务不返回 |

成功与失败的表示：

- 业务失败（会话不存在、参数无效、等待超时等）以 `Ok: false` 加 `Error` 返回。请求处理中抛出的异常（内存不足除外）
  都转换成这样的响应，`Error` 就是异常消息（见第 9.2 节）。
- 少数失败响应仍带数据：`agent-handoff` 保存消息后投递失败时带 `Message` 与 `Delivered: false`；`plugin-run` 失败时带 `PluginResult`；
  `plugin-event` 有插件失败时带 `PluginResults`。
- 少数“没有做任何事”的情况是成功响应：对运行中的会话执行 `session-resume` 返回 `Ok: true` 与 `Text: "Session is already running"`；
  `agent-hook` 忽略事件时返回 `Ok: true` 与说明文字。
- 收不到响应、连接直接关闭的情况：请求超过上限、连接期限到期、后台服务正在停止。

一行完整的原始响应（`ping`）：

```json
{"Ok":true,"Error":null,"SessionId":null,"Sessions":null,"Screen":null,"Text":"CrewTerm 0.3.0","Tasks":null,"Task":null,"Activity":null,"Activities":null,"Notifications":null,"Message":null,"Messages":null,"Delivered":null,"Plugin":null,"Plugins":null,"PluginResult":null,"PluginResults":null,"Version":"0.3.0","ProtocolVersion":2}
```

## 6. 请求类型总览

“返回”一列只列出成功时有值的字段，“—”表示只有 `Ok`。CLI 子命令一列中的“无”表示没有对应的子命令。

| 请求 | 用途 | 必需字段 | 可选字段 | 返回 | CLI 子命令 |
| --- | --- | --- | --- | --- | --- |
| `ping` | 握手，读取版本与协议版本 | — | — | `Text`、`Version`、`ProtocolVersion` | 无（CrewTerm 的客户端连接前自动发送） |
| `stop` | 停止后台服务及其全部终端与任务 | — | — | — | `stop` |
| `list` | 列出后台服务内存中的会话 | — | — | `Sessions` | `sessions` |
| `create` | 启动新的终端会话 | `Command` | `WorkingDirectory`、`Name`、`Columns`、`Rows`、`Provider`、`NativeSessionId`、`ResumeCommand` | `SessionId` | `new` |
| `close` | 结束会话的进程树，保留会话目录中的记录 | `SessionId` | — | — | `close` |
| `screen` | 读取屏幕快照 | `SessionId` | `IncludeText` | `Screen`、`Text` | `read`（只输出 `Text`） |
| `input` | 写入原始终端输入 | `SessionId` | `Text` | — | `send` |
| `resize` | 调整终端尺寸 | `SessionId` | `Columns`、`Rows`（省略时为 100 × 30） | — | 无（界面调整面板尺寸时发送） |
| `task-start` | 提交一条后台命令 | `Command` | `WorkingDirectory`、`Name` | `SessionId`（任务标识） | `run` |
| `pipeline-start` | 提交一条顺序流水线 | `Steps`（至少一步） | `Name` | `SessionId`（任务标识） | 无（界面 F7 的流水线模板） |
| `task-list` | 列出任务（不含输出） | — | — | `Tasks` | `tasks` |
| `task-get` | 查询单个任务（含输出） | `SessionId`（任务标识） | — | `Task` | `task` |
| `task-cancel` | 取消运行中或排队中的任务 | `SessionId`（任务标识） | — | — | `cancel` |
| `agent-state` | 读取语义状态 | — | `SessionId`（省略时返回全部） | `Activity` 或 `Activities` | `agent-state` |
| `agent-report` | 显式报告语义状态 | `SessionId`、`State` | `Source`、`Reason` | `Activity` | `agent-report` |
| `agent-prompt` | 向运行中、未处于 Blocked 的 Agent 提交一条提示词 | `SessionId`、`Text` | — | `SessionId`、`Activity`、`Delivered` | `agent-prompt` |
| `agent-wait` | 等待会话进入目标状态之一 | `SessionId`，`States` 或 `State` | `TimeoutMilliseconds`、`AfterRevision` | `Activity` | `agent-wait` |
| `notifications` | 列出通知，不标记已读 | — | `SessionId` | `Notifications` | `notifications` |
| `notification-read` | 把一条通知标记为已读 | `NotificationId` | — | — | `notification-read` |
| `message-send` | 向收件箱保存一条定向消息，不写入终端 | `FromSessionId`、`TargetSessionId`、`Text` | `InReplyTo` | `Message`、`Delivered`（`false`） | `message-send` |
| `message-list` | 列出收件箱，不确认 | `SessionId`（收件会话） | — | `Messages` | `message-list` |
| `message-ack` | 收件方确认一条消息 | `SessionId`（收件会话）、`MessageId` | — | — | `message-ack` |
| `agent-handoff` | 交接：存入收件箱后作为提示词提交 | `FromSessionId`、`TargetSessionId`、`Text` | `InReplyTo` | `SessionId`、`Message`、`Delivered` | `agent-handoff` |
| `session-catalog` | 列出会话目录（运行中的与休眠的） | — | — | `Sessions` | `sessions --all true` |
| `session-bind` | 登记原生会话标识或恢复命令 | `SessionId`、`Provider`，`NativeSessionId` 与 `ResumeCommand` 至少一个 | — | `SessionId`、`Sessions`（一项） | `session-bind` |
| `session-resume` | 原生恢复已登记的对话 | `SessionId` | `Columns`、`Rows` | `SessionId`、`Text` | `session-resume` |
| `session-restart` | 用原始命令开始新对话 | `SessionId` | `Columns`、`Rows` | `SessionId`、`Text` | `session-restart` |
| `session-forget` | 遗忘已关闭会话的记录 | `SessionId` | — | — | `session-forget` |
| `plugin-list` | 列出已登记的插件 | — | — | `Plugins` | `plugins` |
| `plugin-install` | 登记一个本地插件目录 | `Path` | `Enabled`（默认 `false`） | `Plugin` | `plugin-install` |
| `plugin-enable` | 启用或停用插件 | `PluginId`、`Enabled` | — | `Plugin` | `plugin-enable` |
| `plugin-remove` | 撤销插件登记 | `PluginId` | — | — | `plugin-remove` |
| `plugin-run` | 运行插件中声明的命令 | `PluginId`、`PluginCommand` | `Payload` | `PluginResult` | `plugin-run` |
| `plugin-event` | 立即触发一个插件事件 | `EventName` | `Payload` | `PluginResults` | `plugin-event` |
| `plugin-history` | 读取最近的插件执行结果 | — | — | `PluginResults` | `plugin-history` |
| `agent-hook` | Provider hook 回调 | `SessionId`、`Provider`、`Payload` | — | `Activity` 和/或 `Text` | `agent-hook`（由 Provider 调用） |

## 7. 请求详解

下面的请求示例写成一行，与实际传输的格式相同；响应为便于阅读做了缩进，并省略了值为 `null` 的顶层字段
（实际响应总是一行、包含全部 20 个字段）。示例中的标识、时间与文本只作示意。会话标识是 12 位小写十六进制，
任务、通知与消息标识是 32 位小写十六进制。

### 7.1 服务生命周期

#### `ping`

```json
{"Operation":"ping"}
```

```json
{"Ok": true, "Text": "CrewTerm 0.3.0", "Version": "0.3.0", "ProtocolVersion": 2}
```

`Text` 是旧版客户端也能识别的版本文本；`Version` 是项目版本号，不含 `+` 之后的构建元数据。CrewTerm 的客户端用它判断
后台服务是否在运行（见第 1.1 节与第 2.3 节）。

#### `stop`

```json
{"Operation":"stop"}
```

```json
{"Ok": true}
```

后台服务写回响应后开始停止：不再接受连接，取消后台任务并等待它们记录结果，结束该状态目录下全部终端会话的进程树。
CrewTerm 的客户端只在用户明确执行 `stop` 时发送它；`crewterm stop` 先探测，后台服务未运行时只输出
`CrewTerm daemon is not running.`，不会先启动一个再停止。

### 7.2 会话

#### `list`

```json
{"Operation":"list"}
```

```json
{
  "Ok": true,
  "Sessions": [
    {
      "Id": "3f9c2a7b1d04",
      "Name": "Reviewer",
      "Command": "claude",
      "WorkingDirectory": "D:\\work\\my-app",
      "StartedAt": "2026-10-26T08:15:30.1234567+00:00",
      "HasExited": false,
      "ExitCode": null,
      "Activity": {
        "SessionId": "3f9c2a7b1d04",
        "State": "Working",
        "Source": "hook",
        "Reason": "Claude UserPromptSubmit: provider received a prompt",
        "ChangedAt": "2026-10-26T08:16:02.4418213+00:00",
        "Revision": 42
      },
      "UnreadCount": 0,
      "Priority": 50,
      "Provider": "claude",
      "NativeSessionId": "0b5e3c1e-7f0a-4d43-9a43-3c8f4f0f1f2a",
      "CanResume": true,
      "ResumeReason": "Exact native conversation ID and verified launch options are recorded.",
      "IsDormant": false
    }
  ]
}
```

只包含后台服务内存中的会话：运行中的，以及进程已退出但尚未 `close` 的（`HasExited: true`）。休眠记录用 `session-catalog` 查看。
按 `Priority` 降序、`StartedAt` 升序排列。

#### `create`

```json
{"Operation":"create","Command":"claude","WorkingDirectory":"D:\\work\\my-app","Name":"Reviewer","Columns":120,"Rows":40}
```

```json
{"Ok": true, "SessionId": "3f9c2a7b1d04"}
```

| 字段 | 规则 |
| --- | --- |
| `Command` | 非空白，最多 8192 个字符，不含控制字符 |
| `Name` | 最多 200 个字符，不含控制字符；省略或空白时使用 `Command` |
| `WorkingDirectory` | 绝对路径，省略时为状态目录；保存前去掉末尾分隔符；目录必须存在（不存在时 `Error` 就是该路径） |
| `Columns`、`Rows` | 2–300 列、2–120 行 |
| `Provider` | 省略时按可执行文件名推断；会转为小写 |
| `NativeSessionId` | 必须是精确标识（规则见第 9.1 节）；与命令中选中的标识不同时失败 |
| `ResumeCommand` | 显式登记的恢复命令 |

内存中的会话（含已退出未关闭的）最多 32 个，超过时失败：`Maximum 32 sessions; close unused sessions first`。
已知 Provider 的启动命令会被准备成实际执行的命令（例如 Claude 新对话追加 `--session-id <UUID>`，并接入单次启动的 hook），
会话目录中记录的仍是原始命令。子进程的环境中有 `CREWTERM_SESSION_ID`、`CREWTERM_STATE_DIR`、`CREWTERM_EXE`
（与后台服务同目录的 `crewterm` 可执行文件），会话有 Provider 时还有 `CREWTERM_PROVIDER`。新会话的语义状态为 `Unknown`（来源 `process`）。
进程启动之后的任何一步失败都会结束进程并恢复原有记录，不会留下调用方不知道的会话。
`Provider`、`NativeSessionId`、`ResumeCommand` 的含义见 [原生会话说明](native-sessions.md)。

#### `close`

```json
{"Operation":"close","SessionId":"3f9c2a7b1d04"}
```

```json
{"Ok": true}
```

结束会话的进程树，记录 `Exited` 状态并发出 `session.exited` 插件事件。会话目录中的记录保留为休眠记录，删除它要用 `session-forget`。
会话不在内存中时失败：`Session not found`。

### 7.3 屏幕与输入

#### `screen`

```json
{"Operation":"screen","SessionId":"3f9c2a7b1d04","IncludeText":false}
```

```json
{
  "Ok": true,
  "Screen": {
    "Columns": 120,
    "Rows": 40,
    "CursorColumn": 2,
    "CursorRow": 0,
    "CursorVisible": true,
    "Cells": [
      [
        {"Text": ">", "Foreground": 13686750, "Background": 856343, "Bold": true, "Width": 1},
        {"Text": " ", "Foreground": 13686750, "Background": 856343, "Width": 1}
      ]
    ],
    "History": ["previous output line"],
    "AlternateScreen": false,
    "BracketedPaste": true,
    "ApplicationCursorKeys": false,
    "MouseTracking": 0,
    "SgrMouse": false,
    "Utf8Mouse": false
  }
}
```

示例中的 `Cells` 只保留了一行的两个单元格；实际有 `Rows` 行，每行 `Columns` 个单元格。`IncludeText` 为 `true`（默认）时，
`Text` 是活动缓冲区各行文本（去掉行尾空白）以 `\n` 连接的结果。工作台界面每 160 毫秒轮询一次，带 `IncludeText: false`。
已退出但尚未关闭的会话仍可读取。

#### `input`

```json
{"Operation":"input","SessionId":"3f9c2a7b1d04","Text":"npm test\r"}
```

```json
{"Ok": true}
```

`Text` 原样写入终端，不补回车：`\r` 就是 Enter，`\u0003` 就是 Ctrl+C。这里不过滤控制字符，只受请求大小上限约束。
会话记录的 Provider 是 `claude`、`codex`、`gemini`、`opencode` 或 `copilot`，且 `Text` 以 `\r` 或 `\r\n` 结尾时，后台服务先把语义状态推断为
`Working`（来源 `input`）再写入。进程已退出时失败：`Session has exited`。

#### `resize`

```json
{"Operation":"resize","SessionId":"3f9c2a7b1d04","Columns":160,"Rows":48}
```

```json
{"Ok": true}
```

尺寸超出 2–300 列、2–120 行时失败：`Terminal size must be 2–300 columns and 2–120 rows (Parameter 'columns')`
（行数超出时为 `'rows'`）。

### 7.4 后台任务

#### `task-start`

```json
{"Operation":"task-start","Command":"dotnet test -c Release","WorkingDirectory":"D:\\work\\my-app","Name":"Release tests"}
```

```json
{"Ok": true, "SessionId": "9b1e4c0d2a7f4e5b8c6d1a2b3c4d5e6f"}
```

提交后立即返回任务标识。命令经系统 Shell 执行（Windows 为 `cmd.exe /d /s /c`，其他平台为 `/bin/sh -c`），标准输入立即关闭。
`Command` 非空白、最多 8192 个字符、不含控制字符；`Name` 最多 200 个字符、不含控制字符，省略或空白时使用 `Command`；
`WorkingDirectory` 为绝对路径，省略时为状态目录，目录必须存在。同时运行的任务数取工作区设置中的并发上限
（默认 4，可设 1–32，后台服务启动时读取），其余任务排队。

#### `pipeline-start`

```json
{"Operation":"pipeline-start","Name":"Build and test","Steps":[{"Name":"build","Command":"dotnet build -c Release","WorkingDirectory":"D:\\work\\my-app"},{"Name":"test","Command":"dotnet test -c Release","WorkingDirectory":"D:\\work\\my-app"}]}
```

```json
{"Ok": true, "SessionId": "2c7a4f1e9d3b4a6c8e0f1a2b3c4d5e6f"}
```

整条流水线作为一个任务显示，按顺序执行，任一步失败或被取消即停止。每一步都需要 `Name`（非空白、最多 200 个字符、不含控制字符）、
`Command`（规则同 `task-start`）与 `WorkingDirectory`（绝对路径，必须存在）。`Name` 省略或空白时为 `Pipeline`。
没有步骤时失败：`At least one pipeline step is required`。

#### `task-list`

```json
{"Operation":"task-list"}
```

返回 `Tasks`，最新提交的在前，每项的 `Output` 为空字符串。列表包含排队中、运行中与最近完成的任务；已完成任务在内存与
`task-history.json` 中各保留最近 100 个，后台服务重启后仍可查询。

#### `task-get`

```json
{"Operation":"task-get","SessionId":"9b1e4c0d2a7f4e5b8c6d1a2b3c4d5e6f"}
```

```json
{
  "Ok": true,
  "Task": {
    "Id": "9b1e4c0d2a7f4e5b8c6d1a2b3c4d5e6f",
    "Name": "Release tests",
    "Command": "dotnet test -c Release",
    "WorkingDirectory": "D:\\work\\my-app",
    "StartedAt": "2026-10-26T08:20:11.5530912+00:00",
    "Completed": true,
    "ExitCode": 0,
    "Cancelled": false,
    "Output": "Passed!  - Failed: 0, Passed: 512, Skipped: 0\r\n",
    "Error": null
  }
}
```

任务不存在时失败：`Task not found`。判断任务是否成功要看 `Completed`、`ExitCode`、`Cancelled` 与 `Error`，而不是请求的 `Ok`。

#### `task-cancel`

```json
{"Operation":"task-cancel","SessionId":"9b1e4c0d2a7f4e5b8c6d1a2b3c4d5e6f"}
```

成功只表示已发出取消请求；之后用 `task-get` 等到 `Completed` 再检查 `Cancelled`。任务不存在或已结束时失败：
`Task not found or already completed`。

### 7.5 Agent 状态、通知、消息、交接与等待

语义状态有 `Unknown`、`Working`、`Blocked`、`Done`、`Idle`、`Exited` 六种（见第 8 节），只来自显式证据：
API 上报、Provider hook、用户确认、已提交的输入与提示词，以及少数已识别的审批提示。`Revision` 是全局版本号，
任何状态、通知或消息的修改都会使它递增。状态模型与协作语义见 [Agent 集成指南](agent-integration.md)。

#### `agent-state`

```json
{"Operation":"agent-state","SessionId":"3f9c2a7b1d04"}
```

```json
{
  "Ok": true,
  "Activity": {
    "SessionId": "3f9c2a7b1d04",
    "State": "Done",
    "Source": "ci",
    "Reason": "Build exited with code 0",
    "ChangedAt": "2026-10-26T08:31:47.2209675+00:00",
    "Revision": 57
  }
}
```

不带 `SessionId` 时返回 `Activities`：全部已登记会话（包括休眠记录）的状态，按优先级降序。
会话没有登记时失败：`Agent session not found: <会话标识>`。

#### `agent-report`

```json
{"Operation":"agent-report","SessionId":"3f9c2a7b1d04","State":"Done","Source":"ci","Reason":"Build exited with code 0"}
```

返回记录后的 `Activity`。`Source` 非空白、最多 64 个字符、不含控制字符，省略时为 `api`；`Reason` 非空白、最多 4096 个字符、
除 `\n`、`\r`、`\t` 外不含控制字符，省略时为 `Explicit state report`。状态、来源与原因都与当前相同时不做修改，`Revision` 不变。
状态从其他值变为 `Blocked` 或 `Done` 时生成一条未读通知。当前来源是 `api` 或 `hook` 时，后台服务不再根据屏幕上的审批提示
把该会话改为 `Blocked`。上报只是调用者的声明，后台服务不验证任务是否真的完成。

#### `agent-prompt`

```json
{"Operation":"agent-prompt","SessionId":"3f9c2a7b1d04","Text":"Run the unit tests and report failures"}
```

```json
{
  "Ok": true,
  "SessionId": "3f9c2a7b1d04",
  "Activity": {
    "SessionId": "3f9c2a7b1d04",
    "State": "Working",
    "Source": "prompt",
    "Reason": "An explicit prompt was submitted; Working is inferred until a provider event arrives",
    "ChangedAt": "2026-10-26T08:33:05.6081472+00:00",
    "Revision": 58
  },
  "Delivered": true
}
```

提示词非空白、最多 32768 个字符，除 `\n`、`\r`、`\t` 外不含控制字符。目标会话必须在运行中，且语义状态不是 `Blocked`：
提示词不能被用来替用户回答工具审批，此时的失败消息为
`Agent is blocked. Review the request and respond explicitly in its terminal; prompt delivery does not approve tools.`。
后台服务去掉末尾的回车换行后补一个 `\r` 提交；多行提示词只在目标终端启用了 bracketed paste 时作为一次粘贴整体送入，
否则失败。写入前先把状态记为 `Working`（来源 `prompt`），写入失败时恢复。`Delivered: true` 只说明输入已写入终端，
不证明 Agent 已经执行。

#### `agent-wait`

```json
{"Operation":"agent-wait","SessionId":"3f9c2a7b1d04","States":["Done","Blocked","Exited"],"TimeoutMilliseconds":30000,"AfterRevision":57}
```

返回满足条件的 `Activity`。有 `States` 时使用 `States`，否则使用 `State`，至少要有一个状态。给出 `AfterRevision` 时只接受
`Revision` 严格大于它的状态；省略时，当前状态已匹配便立即返回。`TimeoutMilliseconds` 必须在 1–60000 之间，
超时失败：`Timed out waiting for Agent state: <会话标识>`。同时最多 8 个等待（第 3.1 节）。自己的客户端要把读取超时设得比
`TimeoutMilliseconds` 更长。

#### `notifications`

```json
{"Operation":"notifications","SessionId":"3f9c2a7b1d04"}
```

```json
{
  "Ok": true,
  "Notifications": [
    {
      "Id": "6c0f3e2a9b7d4c1e8f5a2b3c4d5e6f70",
      "SessionId": "3f9c2a7b1d04",
      "State": "Done",
      "Title": "Completed",
      "Reason": "Build exited with code 0",
      "CreatedAt": "2026-10-26T08:31:47.2209675+00:00",
      "Read": false,
      "Source": "ci"
    }
  ]
}
```

按生成顺序排列，最旧的在前；不带 `SessionId` 时返回全部通知。列出不会标记已读。最多保留最近 200 条。

#### `notification-read`

```json
{"Operation":"notification-read","NotificationId":"6c0f3e2a9b7d4c1e8f5a2b3c4d5e6f70"}
```

成功返回 `{"Ok": true}`；通知不存在或已读时失败：`Notification not found or already read`。确认“完成”通知时，若会话仍处于
`Done` 且没有其他未读的完成通知，状态变为 `Idle`（来源 `ack`）。“等待输入”通知标为已读后会话仍是 `Blocked`，这不等于批准。

#### `message-send`

```json
{"Operation":"message-send","FromSessionId":"3f9c2a7b1d04","TargetSessionId":"a71d09e2c5b8","Text":"请检查 parser 改动"}
```

```json
{
  "Ok": true,
  "Message": {
    "Id": "d41c7a9e0b2f4e6a8c1d3e5f7a9b0c2d",
    "FromSessionId": "3f9c2a7b1d04",
    "ToSessionId": "a71d09e2c5b8",
    "Text": "请检查 parser 改动",
    "CreatedAt": "2026-10-26T08:35:12.0917735+00:00",
    "Acknowledged": false,
    "InReplyTo": null
  },
  "Delivered": false
}
```

消息只保存到收件箱，不写入任何终端。两个会话都必须已登记（休眠记录也算）。`Text` 非空白、最多 32768 个字符，
除 `\n`、`\r`、`\t` 外不含控制字符。`InReplyTo` 必须引用一条由收件方发给发件方的消息。最多保留最近 500 条消息。
保存后发出 `message.received` 插件事件。

#### `message-list`

```json
{"Operation":"message-list","SessionId":"a71d09e2c5b8"}
```

返回 `Messages`：发给该会话的全部消息（包括已确认的），按发送顺序排列。列出不会确认消息。

#### `message-ack`

```json
{"Operation":"message-ack","SessionId":"a71d09e2c5b8","MessageId":"d41c7a9e0b2f4e6a8c1d3e5f7a9b0c2d"}
```

`SessionId` 必须是消息的收件方，其他会话不能代为确认（`Message belongs to another recipient (Parameter 'toSessionId')`）。
消息不存在或已确认时失败：`Message not found or already acknowledged`。

#### `agent-handoff`

```json
{"Operation":"agent-handoff","FromSessionId":"3f9c2a7b1d04","TargetSessionId":"a71d09e2c5b8","Text":"请接手修改并报告结果"}
```

```json
{"Ok": true, "SessionId": "a71d09e2c5b8", "Message": {"Id": "…", "FromSessionId": "3f9c2a7b1d04", "ToSessionId": "a71d09e2c5b8", "Text": "请接手修改并报告结果", "CreatedAt": "…", "Acknowledged": false, "InReplyTo": null}, "Delivered": true}
```

处理顺序：先按 `agent-prompt` 的规则校验提示词与目标会话（不合格时直接失败，不保存任何东西），再把消息存入收件箱，
最后把同样的文本作为提示词提交。提交失败时消息已经保存，响应为失败但带着消息（下例中 `Message` 只保留了 `Id`）：

```json
{"Ok": false, "Error": "Message was stored, but terminal delivery failed: Multi-line prompts require the target to enable bracketed paste; send one line or wait until the Agent is ready.", "Message": {"Id": "…"}, "Delivered": false}
```

重试前先检查 `Message` 与收件箱，避免重复提交。

### 7.6 原生会话

会话目录（`native-sessions.json`）保存每个会话的启动命令、Provider 与原生会话标识，进程退出与后台服务重启后仍然保留。
各 Provider 的适配范围见 [原生会话说明](native-sessions.md)。

#### `session-catalog`

```json
{"Operation":"session-catalog"}
```

```json
{
  "Ok": true,
  "Sessions": [
    {
      "Id": "a71d09e2c5b8",
      "Name": "Codex",
      "Command": "codex",
      "WorkingDirectory": "D:\\work\\my-app",
      "StartedAt": "2026-10-25T13:02:44.9081234+00:00",
      "HasExited": true,
      "ExitCode": null,
      "Activity": {
        "SessionId": "a71d09e2c5b8",
        "State": "Exited",
        "Source": "process",
        "Reason": "Saved session is dormant; no terminal process is attached",
        "ChangedAt": "2026-10-26T07:58:01.3349016+00:00",
        "Revision": 3
      },
      "UnreadCount": 0,
      "Priority": 0,
      "Provider": "codex",
      "NativeSessionId": null,
      "CanResume": false,
      "ResumeReason": "Native conversation ID has not been captured. Bind the exact provider ID before resuming; restarting is a new conversation.",
      "IsDormant": true
    }
  ]
}
```

返回会话目录中的全部记录（运行中的与休眠的），排序与 `list` 相同。休眠记录的 `HasExited` 为 `true`、`ExitCode` 为 `null`，
`StartedAt` 是记录最后一次更新的时间。

#### `session-bind`

```json
{"Operation":"session-bind","SessionId":"a71d09e2c5b8","Provider":"codex","NativeSessionId":"5f2b8c4e-9a1d-4e7f-b3c6-2d8e0f1a4b7c"}
```

返回 `SessionId` 与只含这一项的 `Sessions`。`Provider` 必填（`Provider is required`），会转为小写；`NativeSessionId` 与 `ResumeCommand`
至少给一个（`Supply an exact native conversation ID or an explicit custom resume command.`）。这三个字段整体覆盖记录中的值，
传 `null` 的字段会被清空。记录不存在时失败：`Saved session not found: <会话标识>`。

#### `session-resume` 与 `session-restart`

```json
{"Operation":"session-resume","SessionId":"a71d09e2c5b8","Columns":120,"Rows":40}
```

```json
{"Ok": true, "SessionId": "a71d09e2c5b8", "Text": "Resumed the recorded native conversation"}
```

`session-resume` 用记录中的精确标识或登记的恢复命令续接原对话；`session-restart` 用原始命令开始新对话，成功时 `Text` 为
`Restarted as a new conversation`。新进程沿用原来的会话标识。会话仍在运行时，`session-resume` 返回成功与 `Session is already running`，
`session-restart` 失败：`Close the running session before restarting it`。记录不存在时失败：`Saved session not found`；
记录无法恢复时失败，`Error` 说明缺少什么（与 `ResumeReason` 的说明一致）。同样受 32 个会话的上限约束。

#### `session-forget`

```json
{"Operation":"session-forget","SessionId":"a71d09e2c5b8"}
```

删除会话记录、它的语义状态、与它有关的通知和消息，以及为它生成的 `provider-hooks/<会话标识>.json`；Provider 一侧的对话历史保留。
会话仍在运行时失败：`Close the running session before forgetting its record`；记录不存在时失败：`Saved session not found`。

### 7.7 本地插件

清单格式、插件进程收到的 JSON 输入与执行上限见 [本地插件说明](local-plugins.md)。插件标识按不区分大小写比较。
注意清单文件使用 camelCase，而协议中的 `Manifest` 与其他对象一样以 PascalCase 写出。

#### `plugin-list`

```json
{"Operation":"plugin-list"}
```

返回 `Plugins`，按登记顺序排列。

#### `plugin-install`

```json
{"Operation":"plugin-install","Path":"D:\\plugins\\workspace-notes","Enabled":false}
```

```json
{
  "Ok": true,
  "Plugin": {
    "Id": "crewterm.workspace-notes",
    "Name": "Workspace Notes",
    "Version": "1.1.0",
    "SourceDirectory": "D:\\plugins\\workspace-notes",
    "Enabled": false,
    "Manifest": {
      "Id": "crewterm.workspace-notes",
      "Name": "Workspace Notes",
      "Version": "1.1.0",
      "Commands": [
        {
          "Id": "summary",
          "Name": "Summarize workspace files",
          "Executable": "pwsh",
          "Arguments": ["-NoProfile", "-NonInteractive", "-File", "plugin.ps1", "summarize"],
          "TimeoutSeconds": 30,
          "MaxOutputCharacters": 16384,
          "WorkingDirectory": null,
          "Windows": {
            "Executable": "powershell",
            "Arguments": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "plugin.ps1", "summarize"]
          },
          "Linux": null,
          "Osx": null
        }
      ],
      "Hooks": [{"Event": "session.created", "CommandId": "record-event"}]
    }
  }
}
```

示例节选自 `examples/plugins/workspace-notes`，省略了第二条命令与其余 hook。`Path` 必填且必须是绝对路径
（`Local plugin directory is required`、`Local plugin directory must be an absolute path: <路径>`）；`Enabled` 省略时为 `false`。
同一标识已登记时失败：`Plugin ID is already installed: <标识>`。

#### `plugin-enable`

```json
{"Operation":"plugin-enable","PluginId":"crewterm.workspace-notes","Enabled":true}
```

返回更新后的 `Plugin`。`Enabled` 必填（`Enabled state is required`），传 `false` 即停用；插件未登记时失败：`Plugin not installed: <标识>`。

#### `plugin-remove`

```json
{"Operation":"plugin-remove","PluginId":"crewterm.workspace-notes"}
```

只撤销登记，不删除插件目录。插件未登记时失败：`Plugin not installed`。

#### `plugin-run`

```json
{"Operation":"plugin-run","PluginId":"crewterm.workspace-notes","PluginCommand":"summary","Payload":{"workingDirectory":"D:\\work\\my-app"}}
```

```json
{
  "Ok": true,
  "PluginResult": {
    "PluginId": "crewterm.workspace-notes",
    "CommandId": "summary",
    "Success": true,
    "ExitCode": 0,
    "Output": "12 files, 3 directories\n",
    "Error": null,
    "TimedOut": false,
    "OutputTruncated": false,
    "Cancelled": false
  }
}
```

`Ok` 就是 `PluginResult.Success`。失败时 `Error` 取结果中的错误说明，没有时为 `Plugin command failed with exit code <退出码>`。
插件未登记、已停用、命令未声明、超时等都表示为失败的结果。省略 `Payload` 时插件收到 `null`。插件命令最长可运行 300 秒，
自己的客户端要把读取超时设得足够长（`DaemonClient` 为 310 秒）。

#### `plugin-event`

```json
{"Operation":"plugin-event","EventName":"session.created","Payload":{"sessionId":"example-session"}}
```

立即运行所有已启用、订阅该事件的插件 hook，返回每个 hook 的 `PluginResults`。全部成功时 `Ok` 为 `true`（没有订阅者时也是，
`PluginResults` 为空数组），否则 `Error` 取第一个失败的结果。后台服务自己也会在后台发出 `session.created`、`session.exited`、
`agent.state`、`message.received` 事件，这些事件进入容量为 32 的队列按顺序执行，不经过本请求。

#### `plugin-history`

```json
{"Operation":"plugin-history"}
```

返回最近最多 100 条结果（按需运行、事件触发与队列溢出的记录），最旧的在前。

### 7.8 Provider hook 回调

#### `agent-hook`

由 `crewterm agent-hook` 发送，它是 Provider 生命周期回调的被动入口：Claude Code 通过为单次启动生成的
`provider-hooks/<会话标识>.json` 调用 `crewterm --state-dir <状态目录> agent-hook --provider claude --id <会话标识>`，
负载从标准输入传入；Codex 通过 `-c notify=[...]` 调用，JSON 是唯一的位置参数，状态目录与会话标识取自环境变量。
`crewterm agent-hook` 的负载最多 65536 个字符，读取与转发共用 2.5 秒期限；无法识别的事件不联系后台服务；
它从不启动后台服务，也不向标准输出写任何内容。自己的脚本报告状态应使用 `agent-report`。

```json
{"Operation":"agent-hook","SessionId":"3f9c2a7b1d04","Provider":"claude","Payload":{"hook_event_name":"Stop","session_id":"0b5e3c1e-7f0a-4d43-9a43-3c8f4f0f1f2a"}}
```

```json
{
  "Ok": true,
  "Activity": {
    "SessionId": "3f9c2a7b1d04",
    "State": "Done",
    "Source": "hook",
    "Reason": "Claude Stop: native turn finished",
    "ChangedAt": "2026-10-26T08:40:19.7752301+00:00",
    "Revision": 61
  }
}
```

负载与语义状态的对应（`AgentHookEvent.Parse`）：

| Provider | 负载 | 语义状态 |
| --- | --- | --- |
| `claude` | `hook_event_name` 为 `SessionStart` 或 `SessionEnd` | `Idle` |
| `claude` | `UserPromptSubmit` | `Working` |
| `claude` | `PermissionRequest` 或 `StopFailure` | `Blocked` |
| `claude` | `Stop` | `Done`；`background_tasks` 或 `session_crons` 为非空数组时为 `Working` |
| `claude` | `Notification`，`notification_type` 为 `permission_prompt` 或 `idle_prompt` | `Blocked` 或 `Idle`；其他类型忽略 |
| `codex` | `type` 为 `agent-turn-complete` | `Done` |

原生会话标识取自 Claude 的 `session_id` 或 Codex 的 `thread-id`，必须是精确的 UUID。后台服务的处理：

- 无法识别的事件：成功，`Text` 为 `Unknown provider event ignored`。
- 会话不在内存中或进程已退出：成功，`Text` 为 `Hook for closed terminal ignored`。
- `Provider` 与记录不一致：失败，`Hook provider differs from the recorded provider`。
- 事件来自另一个原生对话：记录还没有原生标识，或事件是 Claude 的 `SessionStart` 时，把会话改绑到新标识并保存；
  否则成功但忽略，返回当前 `Activity` 与 `Text`：`Hook from a different native conversation ignored`。
- 其余情况记录状态（来源 `hook`），返回 `Activity`。

其他失败消息：`Hook provider is required`、`Hook payload is required`、`Hook payload must be a JSON object`、
`No verified hook adapter for provider: <名称>`、`Claude hook requires session_id`、`Codex notify requires thread-id`，
以及运行中的会话没有记录时的 `Saved session not found`。

## 8. 返回结构

### SessionInfo

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Id` | 字符串 | 会话标识 |
| `Name` | 字符串 | 显示名称 |
| `Command` | 字符串 | 原始启动命令（重新启动时使用，不含 CrewTerm 为本次启动追加的参数） |
| `WorkingDirectory` | 字符串 | 规范化的工作目录（不带末尾分隔符） |
| `StartedAt` | 时间 | 本次进程的启动时间；休眠记录为最后一次更新记录的时间 |
| `HasExited` | 布尔 | 进程是否已退出；休眠记录为 `true` |
| `ExitCode` | 整数或 `null` | 进程退出码；仍在运行、未知或休眠时为 `null` |
| `Activity` | `AgentActivity` | 最近一次语义状态 |
| `UnreadCount` | 整数 | 未读通知数与未确认的收件消息数之和 |
| `Priority` | 整数 | 排序优先级，按先命中者计：`Blocked` 300，有未读完成通知 200，有未确认收件消息 100，`Working` 50，其余 0 |
| `Provider` | 字符串或 `null` | Provider（小写）；自定义命令为 `null` |
| `NativeSessionId` | 字符串或 `null` | 已捕获的原生会话标识 |
| `CanResume` | 布尔 | 能否精确恢复原生会话 |
| `ResumeReason` | 字符串 | 可以或不能恢复的原因 |
| `IsDormant` | 布尔 | 是否只有记录、没有正在运行的进程 |

### DaemonTaskInfo

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Id` | 字符串 | 任务标识 |
| `Name` | 字符串 | 任务或流水线名称 |
| `Command` | 字符串 | 命令文本；流水线为每行一个 `步骤名: 命令` |
| `WorkingDirectory` | 字符串 | 工作目录；流水线为第一步的目录 |
| `StartedAt` | 时间 | 提交时间（UTC） |
| `Completed` | 布尔 | 是否已结束（成功、失败或取消） |
| `ExitCode` | 整数或 `null` | 真实退出码；未结束、未能启动或排队时被取消为 `null`；流水线取最后执行的步骤 |
| `Cancelled` | 布尔 | 是否被取消 |
| `Output` | 字符串 | 标准输出与标准错误合并后的末尾，最多 262144 个字符；`task-list` 中为空字符串 |
| `Error` | 字符串或 `null` | CrewTerm 自身的错误说明（例如无法启动、历史无法保存）；没有时为 `null` |

### AgentActivity 与 AgentActivityState

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `SessionId` | 字符串 | 会话标识 |
| `State` | 字符串 | 语义状态，见下表 |
| `Source` | 字符串 | 证据来源：`process`（登记、启动、进程退出）、`hook`、`screen`（识别到审批提示）、`ack`（确认完成）、`input`、`prompt`，或上报时给出的值（默认 `api`）；最长 64 个字符 |
| `Reason` | 字符串 | 面向用户的原因说明，最长 4096 个字符 |
| `ChangedAt` | 时间 | 产生此状态的时间（UTC） |
| `Revision` | 整数（64 位） | 产生此状态时的全局版本号，供 `agent-wait` 的 `AfterRevision` 使用 |

| 状态 | 含义 |
| --- | --- |
| `Unknown` | 没有可靠证据：会话刚登记、终端刚启动，或尚未收到显式事件 |
| `Working` | 有显式证据表明一轮任务正在进行 |
| `Blocked` | 需要用户输入或决定（权限请求、已识别的审批提示、Provider 报告失败等） |
| `Done` | 收到了显式的完成事件，对应的完成通知尚未确认 |
| `Idle` | 完成已被确认，或收到了显式的空闲事件 |
| `Exited` | 终端进程已退出（休眠记录也是这个状态）；只描述进程，不代表任务完成 |

### AgentNotification

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Id` | 字符串 | 通知标识 |
| `SessionId` | 字符串 | 触发通知的会话 |
| `State` | 字符串 | `Blocked` 或 `Done` |
| `Title` | 字符串 | `Needs input` 或 `Completed` |
| `Reason` | 字符串 | 与对应状态相同的原因说明 |
| `CreatedAt` | 时间 | 生成时间（UTC） |
| `Read` | 布尔 | 是否已被显式确认为已读 |
| `Source` | 字符串 | 对应状态变化的来源 |

### AgentMessage

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Id` | 字符串 | 消息标识 |
| `FromSessionId` | 字符串 | 发件会话 |
| `ToSessionId` | 字符串 | 收件会话 |
| `Text` | 字符串 | 消息正文 |
| `CreatedAt` | 时间 | 发送时间（UTC） |
| `Acknowledged` | 布尔 | 收件方是否已显式确认 |
| `InReplyTo` | 字符串或 `null` | 所回复的消息标识 |

### TerminalSnapshot 与 TerminalCell

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Columns`、`Rows` | 整数 | 屏幕列数与行数 |
| `CursorColumn`、`CursorRow` | 整数 | 光标位置（从 0 开始） |
| `CursorVisible` | 布尔 | 光标是否可见 |
| `Cells` | `TerminalCell` 二维数组 | 活动缓冲区（主屏幕或备用屏幕）的单元格，按 `[行][列]` 索引 |
| `History` | 字符串数组 | 主屏幕滚动历史的行文本（不含颜色、已去掉行尾空白），最旧的在前，最多 2,000 行 |
| `AlternateScreen` | 布尔 | 是否处于备用屏幕 |
| `BracketedPaste` | 布尔 | 是否启用 bracketed paste（`CSI ? 2004 h`） |
| `ApplicationCursorKeys` | 布尔 | 是否启用应用光标键模式（DECCKM） |
| `MouseTracking` | 整数 | 鼠标跟踪模式：0 关闭，1000 仅按键，1002 另报告按下时的拖动，1003 报告所有移动 |
| `SgrMouse` | 布尔 | 是否使用 SGR 鼠标编码（`CSI ? 1006 h`） |
| `Utf8Mouse` | 布尔 | 是否使用 UTF-8 鼠标编码（`CSI ? 1005 h`） |

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Text` | 字符串 | 单元格的字形（可能含组合字符）；宽字符的续位单元格为空字符串 |
| `Foreground`、`Background` | 整数 | 前景色与背景色，24 位 RGB（`0xRRGGBB`） |
| `Width` | 整数 | 1 普通字符，2 宽字符的首格，0 宽字符的续位格 |
| `Bold`、`Faint`、`Italic`、`Underline`、`Blink`、`Hidden`、`Strikethrough` | 布尔 | 文字样式；为 `false` 时不写出，缺失即 `false` |

### LocalPluginInfo

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `Id` | 字符串 | 插件标识，与清单中的 `Id` 相同 |
| `Name`、`Version` | 字符串 | 显示名称与版本标签（来自清单） |
| `SourceDirectory` | 字符串 | 插件目录的完整路径 |
| `Enabled` | 布尔 | 是否允许执行它的命令与事件 hook |
| `Manifest` | 对象 | 登记时的清单快照：`Id`、`Name`、`Version`、`Commands`、`Hooks` |

`Manifest.Commands` 的每一项有 `Id`、`Name`、`Executable`、`Arguments`、`TimeoutSeconds`、`MaxOutputCharacters`、`WorkingDirectory`，
以及按平台替换调用的 `Windows`、`Linux`、`Osx`（各为 `{"Executable", "Arguments"}` 或 `null`）；`Manifest.Hooks` 的每一项有
`Event` 与 `CommandId`，没有 hook 时为 `null`。

### LocalPluginResult

| 字段 | JSON 类型 | 含义 |
| --- | --- | --- |
| `PluginId` | 字符串 | 插件标识；事件触发时读取插件登记失败为 `catalog`，后台事件队列已满时为 `daemon` |
| `CommandId` | 字符串 | 命令标识；`catalog` 与 `daemon` 两种情况下为事件名 |
| `Success` | 布尔 | 仅当未取消、未超时且进程以退出码 0 结束时为 `true` |
| `ExitCode` | 整数或 `null` | 进程退出码；未启动或未能退出时为 `null` |
| `Output` | 字符串 | 标准输出与标准错误按到达顺序合并后的末尾部分 |
| `Error` | 字符串或 `null` | 失败说明 |
| `TimedOut` | 布尔 | 是否因超时而终止 |
| `OutputTruncated` | 布尔 | 输出是否超过保留上限而丢弃了开头 |
| `Cancelled` | 布尔 | 是否被取消 |

## 9. 输入规则与错误

### 9.1 后台服务入口的校验

| 内容 | 规则 | 失败消息 |
| --- | --- | --- |
| 目标标识（`SessionId`、`FromSessionId`、`TargetSessionId`、`NotificationId`、`MessageId`、`PluginId`、`PluginCommand`、`EventName`） | 需要时必须给出且非空白 | `An explicit target ID is required` |
| 路径（`WorkingDirectory`、步骤的 `WorkingDirectory`、`Path`） | 必须是完整的绝对路径。后台服务的当前目录是程序所在目录，不会替客户端解析相对路径 | `Working directory must be an absolute path: <路径>`、`Local plugin directory must be an absolute path: <路径>` |
| 显示文本：`create` 与 `task-start` 的 `Command`（8192）和 `Name`（200），`pipeline-start` 的 `Name` 与步骤的 `Name`（200）、`Command`（8192） | 不超过括号中的长度；不含任何控制字符（C0、DEL、C1，包括换行与制表符） | `<字段> is too long`、`<字段> must not contain control characters`，字段为 `Command`、`Session name`、`Task name`、`Pipeline name`、`Pipeline step name` |
| 提示词（`agent-prompt`、`agent-handoff` 的 `Text`） | 非空白，最多 32768 个字符，除 `\n`、`\r`、`\t` 外不含控制字符（例如 `^C`、`ESC`） | `Prompt text is required`、`Prompt exceeds 32768 characters`、`Prompt must not contain control characters or terminal escape sequences` |
| 消息正文（`message-send` 的 `Text`） | 同提示词 | `Message exceeds 32768 characters (Parameter 'text')`、`Message must not contain control characters other than line breaks and tabs (Parameter 'text')` |
| 状态来源与原因（`agent-report`） | `Source` 非空白、最多 64 个字符、不含控制字符；`Reason` 非空白、最多 4096 个字符、除 `\n`、`\r`、`\t` 外不含控制字符 | `Activity source or reason is too long`、`Activity source or reason must not contain control characters` |
| 终端尺寸 | 2–300 列、2–120 行 | `Terminal size must be 2–300 columns and 2–120 rows (Parameter 'columns')` |
| 等待 | 至少一个状态；`TimeoutMilliseconds` 为 1–60000 | `At least one valid target state is required (Parameter 'states')`、`Wait timeout must be greater than zero and at most 60 seconds (Parameter 'timeout')` |
| 原生会话标识 | Claude、Codex、Gemini、Copilot：36 个字符、`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` 格式的 UUID；其他 Provider：1–256 个字符，以字母或数字开头，只含字母、数字、`_`、`.`、`:`、`-`；OpenCode 另须以 `ses_` 开头 | `This provider requires a complete native conversation UUID, not a name, prefix, or latest selector.`、`Native conversation ID must be an exact identifier without shell syntax.`、`OpenCode native conversation IDs must start with ses_.` |
| 命令中的 NUL | 会话记录中的启动命令与恢复命令不能含 NUL | `Commands cannot contain NUL characters.` |

原始终端输入（`input` 的 `Text`）不在此列：它本来就是按键，原样写入，只受请求大小上限约束。

### 9.2 错误消息的形式

- `Error` 是一句英文：业务失败为固定文本（例如 `Session not found`、`Task not found`、`Plugin not installed`），
  其余为处理过程中抛出的异常的消息。源码没有定义错误码，客户端应把它显示给用户，而不是按文本分支。
- .NET 参数异常的消息末尾带 ` (Parameter '<参数名>')`，例如第 9.1 节中的尺寸、等待与消息正文错误。
- 工作目录不存在时，`Error` 就是该目录的路径本身（`DirectoryNotFoundException` 的消息）。
- 会话标识在不同操作中的“不存在”消息不同：内存会话为 `Session not found`，语义状态为 `Agent session not found: <标识>`，
  会话目录为 `Saved session not found`（`session-bind` 为 `Saved session not found: <标识>`）。
- 请求不是合法 JSON、字段类型不对或状态名无法识别时，`Error` 是 System.Text.Json 的异常消息（含 `Path: $.字段` 位置）；
  请求行是 `null` 时为 `Empty request`。

## 10. 最小客户端示例

两个示例都发送只读请求 `list` 并打印会话，端点计算与第 2.1 节一致。它们不会启动后台服务：后台服务未运行时连接失败，
可以先执行一次 `crewterm --state-dir <状态目录> sessions`。在 CrewTerm 会话中运行时，可以把 `$CREWTERM_STATE_DIR` 作为状态目录传入。

### 10.1 PowerShell 7

需要 PowerShell 7.4 及以上（.NET 8 起），Windows、Linux、macOS 通用。示例直接调用与 CrewTerm 相同的 .NET API
（`Path.GetFullPath`、`Environment.UserName`、`ToUpperInvariant`、`SHA256.HashData`、`Convert.ToHexString`），因此端点名称与源码完全一致；
并以 `PipeOptions.CurrentUserOnly` 连接，得到与 `DaemonClient` 相同的身份校验。Windows PowerShell 5.1 运行在 .NET Framework 上，
没有其中的部分 API，不能运行此示例。

```powershell
#Requires -Version 7.4
# 列出后台服务中的会话。用法：./crewterm-list.ps1 [-StateDir <状态目录>]
param(
    [string]$StateDir = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CrewTerm')
)
$ErrorActionPreference = 'Stop'

# 与 PipeProtocol.PipeName 相同的算法。
function Get-CrewTermPipeName([string]$StateDirectory) {
    $path = [IO.Path]::GetFullPath($StateDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $utf8 = [Text.Encoding]::UTF8
    if ($IsWindows) {
        $hash = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes($path.ToUpperInvariant()))
        return 'CrewTerm-' + [Environment]::UserName + '-' + [Convert]::ToHexString($hash).Substring(0, 24)
    }
    $socket = [IO.Path]::Combine($path, 'server.sock')
    if ($utf8.GetByteCount($socket) -le 100) { return $socket }
    $hash = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes([Environment]::UserName + '|' + $path))
    return 'crewterm-' + [Convert]::ToHexString($hash).Substring(0, 24).ToLowerInvariant()
}

# 相对路径按 PowerShell 的当前位置解析（[IO.Path]::GetFullPath 用的是进程的当前目录）。
$StateDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StateDir)
$options = [IO.Pipes.PipeOptions]'Asynchronous, CurrentUserOnly'
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', (Get-CrewTermPipeName $StateDir), [IO.Pipes.PipeDirection]::InOut, $options)
try {
    $pipe.Connect(500)    # 500 毫秒内连不上：后台服务未运行或连接已满（TimeoutException）
    $utf8 = [Text.UTF8Encoding]::new($false)
    $writer = [IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
    $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
    $writer.WriteLine((@{ Operation = 'list' } | ConvertTo-Json -Compress))
    $line = $reader.ReadLineAsync()
    if (-not $line.Wait(15000)) { throw 'CrewTerm daemon did not respond within 15 seconds' }
    $response = $line.Result | ConvertFrom-Json
    if (-not $response.Ok) { throw "CrewTerm: $($response.Error)" }
    $response.Sessions | Select-Object Id, Name, HasExited, @{ Name = 'State'; Expression = { $_.Activity.State } }
}
finally { $pipe.Dispose() }
```

构造更复杂的请求时，给 `ConvertTo-Json` 加上 `-Depth`（默认只序列化两层），例如带 `Steps` 或 `Payload` 的请求。

### 10.2 Python

只用标准库，需要 Python 3.8 及以上。Python 没有 .NET 的这些 API，示例按第 2.1 节逐步实现端点计算。

```python
#!/usr/bin/env python3
"""列出 CrewTerm 后台服务中的会话。用法：python crewterm_list.py [状态目录]"""
import hashlib
import json
import os
import socket
import struct
import sys
import time

MAX_SOCKET_PATH_BYTES = 100  # PipeProtocol.MaxSocketPathBytes


def default_state_dir():
    """StateFiles.DefaultDirectory：.NET 的 LocalApplicationData 下的 CrewTerm。"""
    if os.name == "nt":
        return os.path.join(os.environ["LOCALAPPDATA"], "CrewTerm")
    home = os.path.expanduser("~")
    if sys.platform == "darwin":
        return os.path.join(home, "Library", "Application Support", "CrewTerm")
    data = os.environ.get("XDG_DATA_HOME", "")
    if not data.startswith("/"):
        data = os.path.join(home, ".local", "share")
    return os.path.join(data, "CrewTerm")


def dotnet_user_name():
    """Environment.UserName。"""
    if os.name != "nt":
        import pwd
        return pwd.getpwuid(os.geteuid()).pw_name
    import ctypes
    from ctypes import wintypes
    get_user_name = ctypes.WinDLL("secur32", use_last_error=True).GetUserNameExW
    get_user_name.argtypes = [ctypes.c_int, ctypes.c_wchar_p, ctypes.POINTER(wintypes.ULONG)]
    get_user_name.restype = wintypes.BOOLEAN
    size = wintypes.ULONG(0)
    get_user_name(2, None, ctypes.byref(size))  # 2 = NameSamCompatible；第一次调用取得所需长度
    buffer = ctypes.create_unicode_buffer(size.value)
    if not get_user_name(2, buffer, ctypes.byref(size)):
        raise ctypes.WinError(ctypes.get_last_error())
    return buffer.value.split("\\", 1)[-1]  # 去掉“域名\”


def upper_invariant(text):
    """string.ToUpperInvariant()：逐个字符的简单大写映射。"""
    result = []
    for ch in text:
        upper = ch.upper()
        if ch == "\u0131":  # .NET 的不变区域把 ı 保持不变，Python 会映射为 I
            upper = ch
        elif len(upper) != 1:  # ß、连字等：Python 映射为多个字符，无法确定 .NET 的结果
            raise ValueError(f"cannot reproduce ToUpperInvariant() for {ch!r}; use the PowerShell example")
        result.append(upper)
    return "".join(result)


def pipe_address(state_dir):
    """PipeProtocol.PipeName，返回可以直接连接的地址。"""
    path = os.path.abspath(state_dir).rstrip(os.sep)
    if os.name == "nt":
        key = hashlib.sha256(upper_invariant(path).encode("utf-8")).hexdigest().upper()[:24]
        return "\\\\.\\pipe\\CrewTerm-" + dotnet_user_name() + "-" + key
    socket_path = os.path.join(path, "server.sock")
    if len(socket_path.encode("utf-8")) <= MAX_SOCKET_PATH_BYTES:
        return socket_path
    key = hashlib.sha256((dotnet_user_name() + "|" + path).encode("utf-8")).hexdigest()[:24]
    temp = os.environ.get("TMPDIR") or "/tmp/"  # .NET 的 Path.GetTempPath()
    if not temp.endswith("/"):
        temp += "/"
    return temp + "CoreFxPipe_crewterm-" + key  # .NET 把不是绝对路径的管道名放进临时目录


def read_line(read):
    """读取到第一个 \\n 为止，去掉行尾的 \\r。"""
    data = bytearray()
    while True:
        chunk = read(65536)
        if not chunk:
            raise ConnectionError("CrewTerm daemon closed the connection before a complete response")
        end = chunk.find(b"\n")
        if end >= 0:
            data += chunk[:end]
            return data.decode("utf-8").rstrip("\r")
        data += chunk


def call(state_dir, request):
    """一个连接、一行请求、一行响应。"""
    line = (json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    address = pipe_address(state_dir)
    if os.name == "nt":
        deadline = time.monotonic() + 0.5
        while True:
            try:
                pipe = open(address, "r+b", buffering=0)
                break
            except OSError as error:  # 231 = ERROR_PIPE_BUSY：所有管道实例都在处理连接
                if getattr(error, "winerror", None) != 231 or time.monotonic() > deadline:
                    raise
                time.sleep(0.05)
        with pipe:
            view = memoryview(line)
            while view:
                view = view[pipe.write(view):]
            return json.loads(read_line(pipe.read))
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
        sock.settimeout(15)
        sock.connect(address)
        if hasattr(socket, "SO_PEERCRED"):  # Linux：读取后台服务进程的有效用户 ID
            credentials = sock.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("iII"))
            _, uid, _ = struct.unpack("iII", credentials)
        else:  # macOS：退而检查套接字文件的所有者
            uid = os.stat(address).st_uid
        if uid != os.geteuid():
            raise PermissionError(f"CrewTerm daemon socket belongs to uid {uid}, not the current user")
        sock.sendall(line)
        return json.loads(read_line(sock.recv))


def main():
    state_dir = sys.argv[1] if len(sys.argv) > 1 else default_state_dir()
    response = call(state_dir, {"Operation": "list"})
    if not response["Ok"]:
        sys.exit("CrewTerm: " + response["Error"])
    for session in response["Sessions"]:
        state = (session.get("Activity") or {}).get("State", "")
        print(session["Id"], state, session["Name"], sep="\t")


if __name__ == "__main__":
    main()
```

Python 示例与源码的差异与限制：

| 方面 | 说明 |
| --- | --- |
| 大写映射 | .NET 的 `ToUpperInvariant` 逐字符做简单映射，Python 的 `str.upper` 做完整映射。示例按 .NET 的规则保留 `ı`；路径中出现 Python 会映射成多个字符的字符（`ß`、连字等）时报错而不是猜测。两边的 Unicode 版本不同时，极少数新增字母的映射可能不同 |
| 路径规范化 | `os.path.abspath` 与 `Path.GetFullPath` 一样解析 `.` 与 `..`。Windows 上 .NET 还会把 8.3 短文件名（含 `~` 的路径段）展开为长文件名，Python 不会，请传入长文件名形式的路径 |
| 默认状态目录 | Windows 取 `%LOCALAPPDATA%`；Linux 与 macOS 按文档中的默认位置拼出。使用其他状态目录时请作为参数传入 |
| 身份校验 | Linux 用 `SO_PEERCRED` 校验后台服务进程的有效用户 ID；macOS 只检查套接字文件的所有者；Windows 分支不校验管道所有者。需要与 `DaemonClient` 相同的校验时使用 PowerShell 示例 |
| 超时 | Unix 分支整体 15 秒；Windows 分支只有 0.5 秒的连接重试，读取没有超时 |

## 相关文档

- [CLI 使用说明](cli-guide.md)：推荐的调用方式，子命令与本文请求一一对应。
- [架构与分层](architecture.md#4-进程与线程模型)：进程与线程模型、平台差异。
- [Agent 集成指南](agent-integration.md)：语义状态模型、Claude Code 与 Codex 的回调、协作语义。
- [原生会话说明](native-sessions.md)：原生会话标识的记录、恢复与重新开始。
- [本地插件说明](local-plugins.md)：插件清单、插件进程的输入与上限。
- [配置与数据](configuration.md)：状态目录中的文件。
- [开发指南](development.md)：新增请求类型时需要修改的位置。
- [术语表](glossary.md)
