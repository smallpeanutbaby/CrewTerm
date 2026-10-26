# CrewTerm 架构与分层

本文描述 CrewTerm 0.3.x 的分层、依赖规则、工程规范、跨平台设计、进程与线程模型、持久化策略和代码注释规范，
并记录 2026-10-26 各轮架构审查的结论。CrewTerm 支持 Windows、Linux 与 macOS。

## 1. 分层与依赖规则

```
                 ┌──────────────────────────────────────────────┐
                 │  CrewTerm.App  (crewterm)                     │
                 │   ├─ Program / CliCommands / CrewTermHelp      │
                 │   ├─ UI/      Terminal.Gui 工作台界面           │
                 │   └─ Server/  daemon、命名管道协议、任务与插件服务│
                 └───────┬───────────┬───────────┬──────────────┘
                         │           │           │
          ┌──────────────▼──┐ ┌──────▼───────┐ ┌─▼────────────────┐
          │ CrewTerm.Terminal│ │ CrewTerm.    │ │ CrewTerm.         │
          │ 伪终端 + VT 屏幕 │ │ Workspace    │ │ Automation        │
          │ （无项目依赖）   │ │ 文件/Git/检查点│ │ 任务/Agent/插件/原生会话│
          └──────────────────┘ └──────┬───────┘ └──┬───────────────┘
                                      │            │
                               ┌──────▼────────────▼──────┐
                               │ CrewTerm.Core             │
                               │ 路径规则、原子读写、JSON 持久化、│
                               │ 状态文件命名、项目/设置/布局模型 │
                               └───────────────────────────┘
```

规则：

1. **只能向下依赖。** Core 不依赖任何项目；Terminal、Workspace、Automation 互不依赖；只有 App 组合全部模块。
2. **Core 是唯一的基础设施层。** 路径比较与包含判断（`FileSystemPath`）、原子读写（`AtomicFile`）、JSON 状态文件
   （`AtomicJsonStore`）和状态目录命名（`StateFiles`）只在 Core 中实现一次，其他层不得再复制一份，
   也不得在代码中直接书写状态文件名。
3. **业务层不依赖 UI 框架与原生句柄。** Terminal.Gui 只出现在 `App/UI`；伪终端的 P/Invoke（Windows ConPTY 与 Unix libc）
   只出现在 `Terminal/Native`；daemon 进程启动的 P/Invoke 只出现在 `App/Client/DetachedProcess` 与 `App/Client/Native`。
   平台差异收敛在少数入口（`TerminalSession`、`TerminalCommand`、`DetachedProcess`、`PipeProtocol`），上层代码只面对 `ITerminalSession`。
4. **与 UI 无关的交互逻辑放在可单测的小类中**，例如 `TerminalInput`、`TerminalMouse`、`SessionInputDispatcher`、
   `TerminalResizeCoordinator`、`SessionRefreshGate`、`CommandPalette`、`WorkspaceNavigation`；
   `WorkspaceWindow` 只负责组装与界面事件，项目切换时“是否需要先处理未保存编辑”的规则由 `WorkspaceNavigation` 判定。
5. **入口只做分派。** `Program` 解析命令行并分派；daemon 的启动与探测在 `DaemonLauncher`，
   非交互子命令的参数校验与请求构造在 `CliCommands`（参数全部校验通过后才连接或启动 daemon），帮助文本在 `CrewTermHelp`。

## 2. 工程规范

- **一个文件只声明一个类型**（类、记录、结构体、枚举、接口），文件名与类型名一致；嵌套类型一律提取为独立的 `internal` 类型。
  分部类按职责拆分为 `类型名.职责.cs`（如 `DaemonHost.Agents.cs`、`TerminalScreen.Parser.cs`）。
- **项目内按职责分文件夹，命名空间与文件夹一致**（`src/CrewTerm.Core/IO/AtomicFile.cs` 的命名空间是 `CrewTerm.Core.IO`），
  由 `.editorconfig` 的 IDE0130 在构建时强制检查。文件夹名避开会与类型、成员或其他命名空间冲突的名字
  （例如不用 `Screen`、`Layout`、`Text`、`Terminal`、`Workspace`）。测试项目的文件夹与被测项目一一对应，共用夹具放在 `Fixtures/`。
- P/Invoke 声明与原生结构体放在 `Native` 子命名空间（`CrewTerm.Terminal.Native`、`CrewTerm.App.Client.Native`），
  标志位、请求号与信号编号使用具名常量（按平台取值的放在属性中），不在调用处书写魔法数字。
- 只在某个平台可用的类型与成员标注 `[SupportedOSPlatform]`，调用处用 `OperatingSystem.IsWindows()` 等判断保护；
  平台兼容性分析（CA1416）在“警告即错误”下由构建强制检查。
- `Directory.Build.props` 统一 `net10.0`、可空引用、警告即错误，并为所有项目生成 XML 文档：缺少注释（CS1591）、注释格式或 cref 错误（CS1570–CS1574）、
  未使用的 `using`（IDE0005）与命名空间和文件夹不一致（IDE0130）都会使构建失败，规范由编译器强制执行。
- 各测试项目共用 `tests/CrewTerm.Testing`（不引用任何产品项目）：平台限定的测试特性（`WindowsFact`、`UnixFact` 等）、
  按平台选择的测试 Shell 与伪造程序（`TestShell`）、PowerShell 夹具（优先 `pwsh`，Windows 上没有时退回系统自带的 Windows PowerShell 5.1）、
  被测可执行文件定位（`CrewTermBuild`）与条件等待（`Poll`）。跨平台行为写成普通测试，平台专有行为使用平台限定特性，
  不在测试中提前返回。

## 3. 项目结构与各层职责

```
src/
├─ CrewTerm.Core/            基础设施与共享模型（不依赖任何项目）
│  ├─ IO/                    AtomicFile、AtomicJsonStore、FileSystemPath、StateFiles
│  ├─ Projects/              ProjectCatalog、ProjectEntry、WorkspaceNavigation
│  ├─ Settings/              WorkspaceSettings、WorkspaceSettingsStore、CommandTemplate
│  ├─ Layouts/               TerminalLayout、TerminalTab、LayoutNode、LayoutGeometry、LayoutRect、LayoutPlacement、SplitAxis、SplitDirection
│  ├─ Editing/               CodeContext、FileReferenceParser、FileLocation
│  └─ Security/              DisplayText、TerminalPaste
├─ CrewTerm.Terminal/        伪终端与 VT 屏幕（不依赖任何项目）
│  ├─ Pty/                   ITerminalSession、TerminalSession（平台工厂）、ConPtySession（Windows）、UnixPtySession 与 ChildProcessBlock（Linux / macOS）、TerminalCommand
│  ├─ Emulation/             TerminalScreen（Parser/Operations/Colors/Resize 分部类）、TerminalSnapshot、TerminalCell、ScreenBufferState、VtParseState
│  └─ Native/                ConPtyNative 与 Win32 结构体；UnixPtyNative 与 WindowSize（libc）
├─ CrewTerm.Workspace/       以项目根为边界的文件、编辑、Git、检查点（依赖 Core）
│  ├─ Files/                 FileWorkspace、FileEntry、SearchResult、WorkspacePath
│  ├─ Documents/             DocumentBuffer、FileConflictException
│  ├─ Git/                   GitService、GitChange、GitWorktree、GitOperationException、GitResult
│  └─ Checkpoints/           CheckpointStore、CheckpointInfo、CheckpointChange、CheckpointPreview、StoredCheckpoint
├─ CrewTerm.Automation/      命令执行、Agent 状态与协作、原生会话、插件（依赖 Core）
│  ├─ Tasks/                 TaskRunner、TaskRunResult、TaskHistory、CommandTask、BoundedTextBuffer
│  ├─ Pipelines/             PipelineRunner、PipelineResult、PipelineTemplate、PipelineStepTemplate、PipelineTemplateStore
│  ├─ Agents/                AgentCatalog、AgentProfile、AgentAvailability、ExecutableLocator
│  ├─ Activity/              AgentActivityStore（含 Mailbox 分部）、AgentActivity、AgentActivityState、AgentNotification、AgentMessage、AgentActivitySnapshot
│  ├─ Hooks/                 AgentHookEvent、AgentHookLaunchService、AgentHookLaunch
│  ├─ Providers/             ProviderSessionService、ProviderCommandLine、CommandToken、ProviderLaunchPlan、ProviderResumeCapability、NativeSessionStore、NativeSessionRecord
│  └─ Plugins/               LocalPluginCatalog、LocalPluginRunner、LocalPluginManifest、LocalPluginCommand、LocalPluginPlatformCommand、LocalPluginHook、LocalPluginInfo、LocalPluginResult
└─ CrewTerm.App/             crewterm 可执行程序：组合全部模块
   ├─ Program、CrewTermCommandLine、CrewTermHelp、CrewTermVersion   入口分派、启动选项、帮助文本、版本与协议兼容（各部分共用）
   ├─ Cli/                   CliCommands、AgentHookCli（非交互子命令）
   ├─ Client/                DaemonClient、DaemonLauncher、DetachedProcess、Native/（StartupInfo、ProcessInformation）
   ├─ Protocol/              PipeProtocol、DaemonRequest、DaemonResponse、SessionInfo、DaemonTaskInfo
   ├─ Server/                DaemonHost（Agents/Hooks/NativeSessions/Plugins 分部类）、DaemonSession、DaemonTaskService、DaemonTaskEntry、DaemonPluginService
   └─ UI/
      ├─ Windows/            WorkspaceWindow（Actions/Agents/Extensions/Layout/Mouse/Tools 分部类）
      ├─ Controls/           TerminalPane、TerminalLayoutView、PaneDivider、EditorBinding、Dialogs、CommandPalette、PaletteCommand
      └─ Input/              TerminalInput、TerminalMouse、SessionInputDispatcher、SessionRefreshGate、TerminalResizeCoordinator、ResizeSessionState、TerminalSize
tests/                       与 src 一一对应：CrewTerm.<项目>.Tests/<同名文件夹>/；App.Tests 另有 Cli/、Server/、UI/、Tui/（端到端界面测试）
└─ CrewTerm.Testing/         共享测试支持：Platform/（平台限定特性、TestShell）、Fixtures/（PowerShellFixture、CrewTermBuild、Poll）
```

App 内部同样只向下依赖：`UI` 与 `Cli` 通过 `Client` 访问 daemon，`Client` 与 `Server` 都只依赖 `Protocol`（消息类型与管道命名规则
`PipeProtocol.PipeName`）；`Server` 不引用 `UI`、`Cli` 或 `Client`，`Protocol` 不引用任何其他部分。

```
UI ──┐
Cli ─┴─► Client ─► Protocol ◄─ Server
```

## 4. 进程与线程模型

- **UI 进程**：`crewterm`（默认子命令 `ui`）。首次需要时以 `--server` 启动 daemon（`DetachedProcess`，不继承调用方的标准输入输出、
  工作目录为程序所在目录；Linux / macOS 上 daemon 启动后再创建新会话并把标准输入输出改接到 `/dev/null`）。UI 每 160 ms 后台轮询会话列表与屏幕（`screen` 请求带 `IncludeText: false`，只取单元格）；
  所有界面更新通过 `application.Invoke` 回到 UI 线程，并用 `SessionRefreshGate` 丢弃用户切换选择之前发起的过期结果。
- **daemon 进程**：每个状态目录一个（`server.lock` 独占，Unix 上由 `flock` 实现），持有全部伪终端会话、后台任务与插件执行；
  UI 分离（F10）或退出不影响它。命名管道（Unix 上为临时目录中的 Unix 域套接字）仅允许当前用户，**一次连接一个请求、一行 JSON**，
  最多 16 个并发连接；消息按块读取，请求上限 128 Ki 字符、响应上限 8 Mi 字符。
- **伪终端会话**：每个会话一个后台读取线程持续读取输出并喂给 `TerminalScreen`；终端查询回复在屏幕锁之外写回，
  避免与 resize 发生锁顺序反转。关闭会话即结束整个进程树（各平台的做法见下表）。
- **命令解析**：裸程序名只在 `PATH` 的绝对路径条目中查找，绝不查找项目工作目录（防当前目录劫持）；带目录的相对路径按工作目录解析；
  找不到时直接报错，不交给系统回退搜索。

### 平台差异

| 方面 | Windows | Linux / macOS |
| --- | --- | --- |
| 伪终端 | `ConPtySession`：ConPTY（Windows 10 1809+） | `UnixPtySession`：libc `forkpty`，子进程成为新会话首进程，从设备为其控制终端 |
| 启动命令 | 解析出程序的绝对路径后交给 `CreateProcess`；无扩展名时依次尝试 `.exe/.com/.cmd/.bat`，批处理经 `cmd.exe /d /s /c` 启动 | 只接受有执行权限的文件，经 `/bin/sh -c "exec '程序' 其余部分"` 执行：Shell 只解析参数，随后被程序替换 |
| 结束进程树 | 进程挂起创建、加入 kill-on-close 作业对象后再恢复 | 向进程组发送 `SIGHUP`，1 秒后 `SIGKILL`，并 `waitpid` 回收；退出码按 Shell 惯例，被信号结束为 128 + 信号 |
| 子进程环境 | `TERM`、`COLORTERM`；`NoDefaultCurrentDirectoryInExePath=1` 使会话中的 cmd 不从当前目录查找程序 | `TERM`、`COLORTERM`；没有区域设置时补一个 UTF-8 `LANG`；恢复 `SIGPIPE` 默认处理并清空信号屏蔽字 |
| 句柄继承 | 只把伪控制台句柄交给子进程 | 主设备设置 close-on-exec，`forkpty` 与设置之间串行化，其他会话的进程不会继承 |
| daemon 脱离 | `CreateProcessW` 不继承句柄、不创建窗口 | `setsid` 创建新会话，标准输入输出改接 `/dev/null`，关闭终端窗口不会结束 daemon |
| 管道名 | `CrewTerm-用户名-路径哈希` | `crewterm-哈希`（用户名并入哈希），适应 Unix 域套接字约 104 字节的路径长度限制 |
| 状态目录 | `%LOCALAPPDATA%\CrewTerm`（用户级 ACL） | `~/.local/share/CrewTerm` 或 `~/Library/Application Support/CrewTerm`，新建时权限 `0700` |
| Shell 安全 | Provider hook 路径拒绝 `%`、`!`、`"` | 还拒绝 `$`、反引号、反斜杠；含 Shell 展开语法的启动命令需要显式登记恢复命令 |

`fork` 之后、`execve` 之前，子进程只执行预先绑定（`Marshal.Prelink`）、不切换 GC 模式（`SuppressGCTransition`）的原生调用，
所需的字符串与指针数组在 `fork` 之前全部写入非托管内存（`ChildProcessBlock`）：只剩一个线程的子进程中不会发生 JIT、托管分配或运行时锁等待。
Apple Silicon 的可变参数一律经栈传递，`ioctl(TIOCSWINSZ)` 因此使用补齐寄存器的专用声明。
- **后台任务**：`DaemonTaskService` 在线程池中执行，`admission` 信号量限制并发并使排队任务可无副作用地取消。

## 5. 状态目录与持久化

状态目录默认为 .NET 的 `LocalApplicationData` 下的 `CrewTerm`（Windows `%LOCALAPPDATA%\CrewTerm`、Linux `~/.local/share/CrewTerm`、
macOS `~/Library/Application Support/CrewTerm`），由 `StateFiles.EnsureDirectory` 创建（Unix 上为 `0700`），文件名统一定义在 `StateFiles`：

| 文件 | 写入方 | 并发策略 |
| --- | --- | --- |
| `projects.json`、`workspace.json` | UI | 乐观并发：`SaveIfUnchanged`，文件被其他客户端改过则拒绝覆盖 |
| `agent-profiles.json`、`pipeline-templates.json` | UI | 悲观并发：`AtomicJsonStore.Update` 在命名互斥锁内读-改-写 |
| `task-history.json`、`native-sessions.json`、`local-plugins.json` | daemon（插件登记也可由 CLI） | 同上 |
| `agent-activity.json`、`saved-sessions.json` | daemon | 单写者（`server.lock` 保证），`AtomicJsonStore.Save` |
| `provider-hooks/<会话>.json` | daemon | 每次启动覆盖；`session-forget` 时删除 |
| `checkpoints/<路径哈希>/` | UI | 每个检查点只写一次（`overwrite: false`） |
| `server.lock`、`server-error.log` | daemon | 独占锁 / 追加 |

所有写入都经过 `AtomicFile.WriteAllBytes`：同目录临时文件 → WriteThrough 刷盘 → 可选的提交前检查 → 原子替换
（目标被杀毒软件或其他读取方短暂占用时有限次重试，最长约 0.5 秒）。状态文件的读取使用 `AtomicFile.ReadAllBytes`，
以读写删除共享方式打开并一次读完，占用时间极短；读取与替换恰好重叠时由替换方重试完成。源代码保存与检查点恢复在提交前检查中再次比对磁盘内容，Agent 并发修改时抛出 `FileConflictException`，绝不静默覆盖。

## 6. 代码注释规范

- 所有类型、构造函数、方法、属性、事件、字段（**包括私有字段与常量**）和枚举成员都写中文 XML 文档注释；测试代码同样适用。
- `<summary>` 一律使用三行格式，不写单行 `/// <summary>…</summary>`：

  ```csharp
  /// <summary>
  /// 上次选中的项目标识。
  /// </summary>
  public string? LastProjectId { get; set; }
  ```

- 有参数、返回值或会抛出约定异常时补充 `<param>`、`<returns>`、`<exception>`；位置记录与主构造函数的参数在类型上用 `<param>` 说明；
  行为约束（线程安全、需要持有的锁、并发策略、上限、单位）写在 `<remarks>`。
- 局部函数不能使用 XML 注释，改用普通 `//` 注释说明用途。
- 方法体内只在“为什么这样做”不明显时写 `//` 注释，不复述代码。

## 7. 审查结论

### 第一轮（v0.1 基线）

| 问题 | 位置 | 处理 |
| --- | --- | --- |
| 原子写入实现重复 4 份（JSON×2、源码保存、检查点） | Core / Automation / Workspace | 统一为 `AtomicFile` |
| 路径比较规则分散在 5 处 | Core / Workspace / UI | 统一为 `FileSystemPath` |
| 状态文件名以字符串字面量散落在多个程序集 | 全部 | 统一为 `StateFiles` |
| Agent 配置、流水线模板只有进程内锁，多个 UI 客户端同时保存会互相覆盖 | Automation | 改用跨进程 `AtomicJsonStore.Update` |
| 任务输出每个块都整体前移，大量输出时退化为平方级复制 | `TaskRunner`、`DaemonTaskService` | 新增 `BoundedTextBuffer` |
| 运行中任务每收到一块输出就重建 256K 字符的快照字符串 | `DaemonTaskService` | 改为读取时才生成 |
| daemon 内存中永久保留所有已完成任务及其输出 | `DaemonTaskService` | 只保留最近 `retainedTasks` 个 |
| daemon 继承调用方当前目录，导致该目录无法删除/重命名 | `DetachedProcess` | 工作目录改为程序所在目录 |
| worktree 路径带末尾分隔符时创建成功却抛出异常 | `GitService` | 路径先规范化 |
| `git status` 与 Agent 的 git 操作争抢 `index.lock` | `GitService` | 设置 `GIT_OPTIONAL_LOCKS=0` |
| 文件区只隐藏小写 `.git` | `FileWorkspace` | 不区分大小写 |

### 第二轮（v0.2 完成后，整体工程化）

| 问题 | 位置 | 处理 |
| --- | --- | --- |
| **安全**：裸命令名先在项目目录中查找，打开不受信任的仓库时会执行其中同名的 `cmd.exe`、`ssh.cmd` 等（当前目录劫持） | `TerminalCommand` | 裸程序名只查 `PATH`，`.\` 相对路径才查工作目录，找不到即报错；新增安全回归测试 |
| VT 私有前缀：`CSI > 1 u` 被当作恢复光标、`CSI > 4;1 m` 被当作加粗、`CSI ? 2;3 r` 被当作设置滚动区域；带中间字节的 `CSI 1 SP @` 被当作 ICH | `TerminalScreen.Parser` | 按前缀分派，只执行明确支持的私有序列；忽略带中间字节的序列；CSI 中的 C0 控制字符立即执行；`ESC %`/`ESC SP` 不再打印末字节 |
| UI 每 160 ms 的屏幕响应同时带整屏文本，且每个单元格序列化全部样式字段；协议逐字符异步读取 | `Protocol`、`DaemonHost`、`TerminalCell` | `screen` 支持 `IncludeText: false`；默认值样式不序列化；按 8K 字符块读取（均向后兼容） |
| `crewterm stop` 在 daemon 未运行时先启动一个再停止；缺少 ID 时报 “Session not found”；参数错误也会先拉起 daemon | `Program` | 拆出 `CliCommands`/`DaemonLauncher`：先校验参数，`stop` 只探测不启动，缺少 ID 报明确的参数错误 |
| daemon `create` 未去掉工作目录末尾分隔符，UI 无法把会话匹配到项目 | `DaemonHost`、`NativeSessionStore` | 统一 `FileSystemPath.Normalize` |
| 分离 HEAD 的 worktree 显示为空分支名 | `WorkspaceWindow.Tools` | 依据 `IsDetached` 显示 `detached <短哈希>` |
| UI 的项目切换规则与 `WorkspaceNavigation` 各写一份，且多处路径比较写死忽略大小写 | `WorkspaceWindow*` | 当前项目由 `WorkspaceNavigation` 持有并判定；路径比较统一 `FileSystemPath` |
| 读取与另一进程的原子替换恰好重叠时，替换因共享冲突直接失败 | `AtomicFile`、`AtomicJsonStore` | 替换时对暂时性共享冲突有限次重试；读取统一用最宽松共享方式的 `AtomicFile.ReadAllBytes` |
| 命令模板条目为 `null` 或字段为空时加载成功，UI 使用时崩溃 | `WorkspaceSettingsStore` | 加载与保存时校验每个模板 |
| v0.2 新增状态文件名、`server.lock`、`server-error.log` 在多处硬编码 | Automation / App | 全部收归 `StateFiles` |
| 插件路径越界判断写死忽略大小写 | `LocalPluginCatalog` | 改用 `FileSystemPath.IsWithin` |
| 遗忘会话后其 provider hook 配置文件永久残留 | `DaemonHost.Agents` | `session-forget` 时删除 |
| 后台刷新线程读取的 `polling`/`detached` 未声明为 volatile | `WorkspaceWindow` | 声明为 `volatile` |
| 示例插件与 26 个测试硬依赖 PowerShell 7，普通 Windows 上无法运行 | `examples/`、测试 | 示例改用系统自带 Windows PowerShell（`-ExecutionPolicy Bypass`）；测试统一 `PowerShellFixture` |
| `AgentStateTracker` 已被 `AgentActivityStore` 取代，仅剩测试引用；`AtomicJsonFile` 只是转发到 Core 的空壳 | Automation | 删除死代码，测试场景已由 `AgentActivityStoreTests` 覆盖 |
| 30 个文件声明多个类型、11 处嵌套类型 | 全部 | 一个文件一个类型 |
| `Program` 同时承担入口、daemon 启动、CLI 请求构造和帮助文本 | App | 拆分为 `Program`、`DaemonLauncher`、`CliCommands`、`CrewTermHelp` |
| **安全**：会话名、状态原因、Agent 消息、插件名称与任务输出原样显示在工作台中，夹带 `ESC` 序列时可能被宿主终端执行（Agent 可被提示注入） | daemon 入口、Automation、UI | 新增 `DisplayText`：daemon 与存储层拒绝控制字符；对话框、列表与任务输出显示前去掉 ANSI 序列 |
| **安全**：原生会话 ID 校验的正则用 `$`（允许末尾换行），`Guid` 解析容忍首尾空白，ID 会被拼入恢复命令 | `ProviderSessionService`、`LocalPluginCatalog` | 改用 `\z` 并要求 UUID 恰好 36 字符 |
| `codex resume -- 文本` 把 `--` 后的提示词当作恢复选中的会话 | `ProviderCommandLine` | `--` 之后不再消费会话标识 |
| 读取 Codex 配置时正则超时异常未捕获，导致启动失败而不是保守跳过 hook | `AgentHookLaunchService` | 一并按“无法检查、保留现有配置”处理；删除未使用的参数 |
| 确认通知时先落盘“已读”再查找会话，状态不一致时留下半完成的修改 | `AgentActivityStore` | 先校验再提交 |
| daemon 运行中被释放时，插件服务与关闭信号先被释放，`RunAsync` 收尾时访问已释放对象 | `DaemonHost`、`DaemonPluginService` | 停止可重入；运行期间由 `RunAsync` 负责最终释放 |
| 被截断的 `CSI 38;5 m` 把子参数 `5` 当作“闪烁” | `TerminalScreen.Colors` | 扩展颜色的子参数无论是否完整都一并跳过 |
| 尺寸越界时异常的参数名总是 `width`/`columns` | `LayoutGeometry`、`DaemonHost` | 分别报告 |
| 上次打开的项目目录被删除后工作台无法启动；未打开文件时 F5 显示 “Saved ”；选中裸路径无法打开；resize 失败成为未观察异常；会话 ID 过短时标题截取越界；代码上下文绕过输入队列可能与按键交错；任务输出对话框的标志跨线程读写；resize 状态永不清理 | UI | 逐项修复 |
| 测试：临时目录不清理、写入系统 `%TEMP%` 与仓库 `work/`、`ping -t` 子进程可能泄漏、Codex 用例读取本机真实配置、慢机器上超时、版本号硬编码、两个测试名声称的场景未覆盖 | tests | 逐项修复，并为本轮所有修复补充回归测试 |

### 第二轮的独立复审（修复后再审）

| 问题 | 位置 | 处理 |
| --- | --- | --- |
| **安全**：npm 启动脚本（`codex.cmd` 等）以裸名调用 `node`，cmd.exe 先在当前目录（项目目录）查找，仓库中植入的 `node.bat` 会被执行 | `ConPtySession` | 会话环境设置 `NoDefaultCurrentDirectoryInExePath=1`，会话内的 cmd.exe 不再从当前目录查找程序 |
| **安全**：剪贴板与代码上下文中的 `ESC [ 201 ~` 可提前结束 bracketed paste 并执行其后的命令；只含 `\r` 的多行粘贴不提示 | UI | 新增 `TerminalPaste`：送入终端前去掉 `ESC` 与 C1 控制字符；回车也视为换行 |
| **安全**：提示词中的 `\r`、`^C`、`^D` 可多次提交、中断 Agent 或抢答随后出现的审批 | `DaemonHost.Agents` | 拒绝换行/制表符以外的控制字符；多行提示词仅在目标启用 bracketed paste 时整体投递 |
| 相对的 `--project`/`--path` 被 daemon 按安装目录解析，静默在错误目录启动 | CLI、UI、daemon | 客户端转换为绝对路径；daemon 拒绝非绝对路径 |
| 一个字母后跟海量组合符可让单元格无限增长（平方级复制、快照超过协议上限） | `TerminalScreen` | 单个字形簇上限 64 个 UTF-16 单元 |
| 恢复/重新启动的会话固定为 100×30，且旧的已确认尺寸使 resize 永不重发 | UI | 以当前面板尺寸启动并清除该会话的 resize 状态 |
| 会话被恢复为新进程时，退出监视可能把新会话误标为已退出 | `DaemonHost.Agents` | 在 `changeLock` 内确认仍是当前对象后才记录退出 |
| 默认 JSON 编码把中文写成 `\uXXXX`，中文粘贴/提示词的实际上限缩小约三分之二 | `Protocol` | 管道上使用宽松转义（UTF-8 原样） |
| 流水线名称与步骤名未校验控制字符；任务名直接显示 | daemon、UI | 校验并在显示前清理 |
| SGR 58/59 未消费子参数；截断的冒号形式颜色吞掉后续参数；超长 CSI 的剩余字节被打印 | `TerminalScreen` | 逐项修复 |
| Agent 发现接受 PATH 中的相对条目（如 `.`） | `ExecutableLocator` | 忽略相对条目 |
