# 开发者指南

本文面向想修改 CrewTerm 源码的开发者：如何搭建环境、构建、调试、运行和编写测试，常见改动的完整步骤，以及发布流程。
提交规范与评审要求见 [贡献指南](../CONTRIBUTING.md)，分层设计与审查结论见 [架构文档](architecture.md)。

## 目录

- [环境准备](#环境准备)
- [仓库结构](#仓库结构)
- [构建与运行](#构建与运行)
- [调试](#调试)
- [测试](#测试)
- [常见改动的步骤](#常见改动的步骤)
- [持续集成](#持续集成)
- [发布](#发布)
- [文档](#文档)

## 环境准备

| 工具 | 说明 |
| --- | --- |
| .NET SDK | 版本由 `global.json` 固定为 `10.0.302`（`rollForward: latestFeature`，接受更高的 10.0 功能版本） |
| Git | 构建不需要，但 Workspace 测试会创建真实的 Git 仓库 |
| PowerShell 7（`pwsh`） | Linux / macOS 上运行全部测试需要；Windows 上没有 `pwsh` 时，测试夹具自动退回系统自带的 Windows PowerShell 5.1 |
| 编辑器 | 任何支持 `.slnx` 解决方案格式与 .NET 10 的 IDE 或编辑器 |

终端建议使用 Windows Terminal、iTerm2、GNOME Terminal、WezTerm 等支持真彩色的程序，窗口至少 120 × 35。

## 仓库结构

```
CrewTerm/
├─ src/                 五个产品项目（Core、Terminal、Workspace、Automation、App），见架构文档 §3
├─ tests/               与 src 一一对应的测试项目，以及共享测试支持 CrewTerm.Testing
├─ docs/                用户、集成与开发文档；docs/verification/ 为各版本的验证记录
├─ examples/plugins/    可以直接安装运行的本地插件示例
├─ scripts/             publish.ps1 / publish.sh（发布包）、verify.ps1 / verify.sh（提交前检查）
├─ .github/workflows/   ci.yml（三平台构建与测试）、release.yml（打标签后发布）
├─ CrewTerm.slnx        解决方案
├─ Directory.Build.props  全部项目共用的编译设置（警告即错误、XML 文档、代码风格）
├─ .editorconfig        代码风格规则（构建时强制执行）
└─ global.json          固定 SDK 版本
```

`artifacts/`（发布输出）与 `work/`（本地临时目录）已被 `.gitignore` 忽略。

## 构建与运行

```sh
dotnet build CrewTerm.slnx -c Release
dotnet run --project src/CrewTerm.App -c Release -- --state-dir ./work/dev-state --project .
```

开发时请始终：

- **使用 `-c Release`**。你日常使用的 CrewTerm 后台服务如果是从 `bin/Debug` 启动的，会一直锁定那里的程序集，Debug 构建会因文件被占用而失败。
- **使用独立的 `--state-dir`**。开发中的版本与日常使用的版本各自拥有一个后台服务，互不影响；不要让开发版本连接或停止你正在使用的服务。
- 用完后停止开发用的后台服务：

```sh
dotnet run --project src/CrewTerm.App -c Release -- --state-dir ./work/dev-state stop
```

所有 CLI 子命令都可以这样运行，例如 `-- --state-dir ./work/dev-state sessions`。完整的子命令说明见 [CLI 使用说明](cli-guide.md)。

## 调试

**界面进程**：直接在 IDE 中以 `src/CrewTerm.App` 为启动项目、参数 `--state-dir <目录> --project <目录>` 启动。
界面需要真实终端，IDE 的输出窗口不能显示 TUI，请把调试控制台设置为外部终端，或先在终端中启动，再把调试器附加到 `crewterm` 进程。

**后台服务**：界面在需要时以 `crewterm --server --state-dir <目录>` 启动一个脱离终端的后台服务。调试服务端时：

1. 先停止该状态目录的服务（`crewterm --state-dir <目录> stop`）；
2. 在 IDE 中以 `--server --state-dir <目录>` 启动 `src/CrewTerm.App`，或在终端中运行后附加调试器
   （Linux / macOS 上服务启动后会创建新会话并把标准输入输出改接到 `/dev/null`，附加调试器更方便）；
3. 再用另一个终端以同一个 `--state-dir` 打开界面或运行 CLI 子命令，它们会连接到正在调试的服务。

**日志与状态**：服务启动失败时，异常会追加到状态目录中的 `server-error.log`；所有持久状态都是状态目录中的 JSON 文件，
可以直接查看（字段说明见 [配置与数据参考](configuration.md)）。调试时修改状态文件之前，请先停止服务。

**终端仿真问题**：`TerminalScreen` 只依赖输入字节，最快的办法是在 `tests/CrewTerm.Terminal.Tests/Emulation/` 中写一个把问题字节序列直接喂给屏幕的测试，
而不是在完整界面中复现。

## 测试

```sh
dotnet test CrewTerm.slnx -c Release                       # 全部测试
dotnet test tests/CrewTerm.Core.Tests -c Release           # 单个项目
dotnet test CrewTerm.slnx -c Release --filter "FullyQualifiedName~AtomicFile"   # 按名称过滤
./scripts/verify.sh                                        # 提交前：构建 + 全部测试 + git diff --check（Windows 用 verify.ps1）
```

### 测试项目

| 项目 | 覆盖范围 |
| --- | --- |
| `CrewTerm.Core.Tests` | 原子读写、状态文件、路径规则、项目与设置存储、布局树、安全文本处理 |
| `CrewTerm.Terminal.Tests` | VT 解析与屏幕操作、宽字符、滚动历史；真实 ConPTY / `forkpty` 子进程的输入、输出、缩放与进程树结束 |
| `CrewTerm.Workspace.Tests` | 真实文件与 Git 仓库上的文件浏览、编辑保存、冲突检测、Git 改动、worktree、检查点 |
| `CrewTerm.Automation.Tests` | 后台任务与流水线、Agent 目录与程序查找、语义状态与收件箱、Provider 命令行与原生会话、hook、本地插件 |
| `CrewTerm.App.Tests` | 命名管道 / 域套接字协议与 daemon（`Server/`）、CLI 进程（`Cli/`）、界面输入逻辑（`UI/`）、端到端界面（`Tui/`） |
| `CrewTerm.Testing` | 不是测试项目：各测试项目共用的平台特性、测试 Shell 与夹具，不引用任何产品项目 |

### 共享测试支持（`CrewTerm.Testing`）

| 类型 | 用途 |
| --- | --- |
| `WindowsFact` / `WindowsTheory`、`UnixFact` / `UnixTheory` | 只在对应平台运行的测试；不要用提前 `return` 跳过 |
| `TestShell` | 当前平台的测试 Shell 命令与伪造程序 |
| `PowerShellFixture` | 需要真实 PowerShell 进程的测试；优先 `pwsh`，Windows 上没有时使用 Windows PowerShell 5.1 |
| `CrewTermBuild` | 仓库根目录与被测 `crewterm` 可执行文件的位置 |
| `Poll` | 条件等待；不要用固定的 `Task.Delay` 等待异步结果 |
| `CanonicalPath` | 规范化临时目录路径（例如 macOS 的 `/private/var` 符号链接），便于比较 |

### 端到端界面测试

`tests/CrewTerm.App.Tests/Tui/` 中的测试在 CrewTerm 自己的伪终端中启动真实的 `crewterm` 可执行文件，连接独立临时状态目录中的真实 daemon，
通过写入按键的转义序列（例如 F8 是 `ESC [19~`）操作界面，再读取屏幕文本断言结果。这些测试属于 `TuiCollection`，
彼此之间以及与本程序集的其他测试都不并行，避免在核心较少的机器上因调度延迟超时。

新增界面测试时：

- 每个测试使用新的临时状态目录，并在 `finally` 中发送 `stop` 请求、用 `TestStateDirectory.DeleteAsync` 清理；
- 按屏幕内容等待（例如等到出现 `F10 Detach`），不要按固定时长等待；
- 加上 `[Collection(TuiCollection.Name)]`。

### 测试发布产物

默认情况下，CLI 与界面测试运行与测试程序集同一构建配置下的 `src/CrewTerm.App/bin/<配置>/net10.0/crewterm`。
把环境变量 `CREWTERM_TEST_EXE` 指向发布包中的可执行文件，这些测试就会改为验证发布产物：

```sh
./scripts/publish.sh
CREWTERM_TEST_EXE="$PWD/artifacts/linux-x64/crewterm" dotnet test tests/CrewTerm.App.Tests -c Release
```

## 常见改动的步骤

### 新增一个 CLI 子命令（以及对应的 daemon 请求）

1. **协议**：如需新字段，在 `src/CrewTerm.App/Protocol/DaemonRequest.cs` / `DaemonResponse.cs` 中添加（可空、带默认值，保持旧客户端兼容）。
2. **服务端**：在 `src/CrewTerm.App/Server/DaemonHost*.cs` 的请求分派中处理新操作名；在入口校验输入（绝对路径、控制字符、长度上限），
   不要信任客户端。
3. **协议兼容**：旧版 daemon 不认识的操作要在 `CrewTermVersion.RequiresCurrentProtocol` 中返回 `true`（默认即为 `true`）；
   协议语义发生不兼容变化时提高 `CrewTermVersion.ProtocolVersion`。
4. **客户端**：在 `src/CrewTerm.App/Cli/CliCommands.cs` 中把子命令映射为请求，所有参数校验完成后才连接或启动 daemon。
5. **帮助**：更新 `src/CrewTerm.App/CrewTermHelp.cs`（`--help` 与 F1 共用）。
6. **测试**：`tests/CrewTerm.App.Tests/Server/` 中测试协议行为，`tests/CrewTerm.App.Tests/Cli/` 中通过真实 CLI 进程测试参数与退出码。
7. **文档**：[CLI 使用说明](cli-guide.md)、[协议参考](protocol.md)、`CHANGELOG.md` 的“未发布”部分。

### 新增一条命令面板命令

1. 在 `src/CrewTerm.App/UI/Windows/WorkspaceWindow.Actions.cs` 的 `Palette()` 中加入 `new("稳定ID", "分组: 显示文本", 方法)`；
   界面中的命令面板只按显示文本过滤（ID 目前只用于 `CommandPalette` 的单元测试），显示文本沿用现有的 `Agent:`、`Terminal:`、
   `Editor:` 等分组前缀。
2. 与界面无关的逻辑放进可单独测试的小类（参见架构文档 §1 第 4 条），`WorkspaceWindow` 只负责组装。
3. 在 `tests/CrewTerm.App.Tests/Tui/` 中写一个端到端测试：打开 F9、输入过滤文字、回车执行、断言屏幕。
4. 更新 [快捷键与命令参考](commands.md) 与 [使用手册](user-guide.md)。

### 新增一个内置 Agent / Provider

1. 在 `src/CrewTerm.Automation/Agents/AgentCatalog.cs` 的内置列表中加入配置（ID、显示名、可执行文件名）。
2. 如果支持原生会话恢复：在 `src/CrewTerm.Automation/Providers/` 中加入该 Provider 的精确会话选择器与可保留的参数规则；
   参数语义不确定的一律要求显式恢复命令，不要猜测。
3. 如果 Provider 提供生命周期回调：在 `src/CrewTerm.Automation/Hooks/` 中实现“只对本次启动生效”的接入，不修改用户的全局配置。
4. 在 `tests/CrewTerm.Automation.Tests/` 中用可控的伪造程序验证命令构造与回调；不要依赖真实的付费模型调用。
5. 更新 [Agent 集成指南](agent-integration.md)、[原生会话](native-sessions.md) 与 README 的特性列表。

### 新增一个状态文件

1. 在 `src/CrewTerm.Core/IO/StateFiles.cs` 中定义文件名常量，其他代码只通过 `StateFiles.PathOf` 取路径，不直接书写文件名。
2. 读写使用 `AtomicJsonStore` / `AtomicFile`（临时文件 → 刷盘 → 原子替换），损坏的文件保留而不覆盖。
3. 在 [配置与数据参考](configuration.md) 中补充该文件的用途、格式与能否手动编辑。

### 平台相关的代码

- 只在某个平台可用的类型或成员标注 `[SupportedOSPlatform("windows")]` 等，调用处用 `OperatingSystem.IsWindows()` 保护；
  平台兼容性分析（CA1416）在警告即错误下由构建检查。
- P/Invoke 声明只放在 `CrewTerm.Terminal.Native` 与 `CrewTerm.App.Client.Native` 命名空间中，常量具名，不在调用处写魔法数字。
- 平台差异尽量收敛在 `TerminalSession`、`TerminalCommand`、`DetachedProcess`、`PipeProtocol` 等少数入口，上层代码只面对 `ITerminalSession`。
- 本地只有一个平台时，另外两个平台的行为由持续集成验证；推送后请确认三个平台都通过。

## 持续集成

`.github/workflows/ci.yml` 在每次推送和拉取请求时运行，矩阵为 `windows-latest`、`ubuntu-latest`、`macos-latest`（Apple Silicon）：

1. `dotnet build CrewTerm.slnx -c Release`；
2. **逐个**运行测试项目（多个程序集同时启动大量真实进程时，共享运行器负载过高，计时类测试会无谓地超时）；
3. 失败时把失败测试写成检查注释：先输出一条列出全部失败测试名的汇总，再逐条输出消息与堆栈（每步最多显示 10 条注释）；
4. 上传 `.trx` 测试结果作为构件。

本地复现某个平台的失败时，先看注释中的测试名与消息，再在 `TestResults` 构件中查看完整输出。

## 发布

1. 在 `main` 上更新 `src/CrewTerm.App/CrewTerm.App.csproj` 中的 `<Version>`，把 `CHANGELOG.md` 的“未发布”改为版本号与日期，并更新比较链接。
2. 推送 `vX.Y.Z` 标签。`.github/workflows/release.yml` 先调用 CI 在三个平台上运行全部测试，通过后在对应系统上为
   `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 生成自带运行时的发布包
   （macOS 的启动程序需要在 macOS 上签名，否则 Apple Silicon 拒绝运行）。
3. Windows 包为 `crewterm-<标签>-<RID>.zip`，Linux / macOS 包为保留执行权限的 `crewterm-<标签>-<RID>.tar.gz`；
   全部上传到自动生成说明的 GitHub Release。

本地生成发布包：

```sh
./scripts/publish.sh [linux-x64|linux-arm64|osx-x64|osx-arm64]     # Linux / macOS
./scripts/publish.ps1 -Runtime win-x64                              # Windows，或任意平台的 PowerShell 7
```

发布目录包含可执行文件、`README.md`、`LICENSE`、`CHANGELOG.md`、`docs/` 下的文档与插件示例；Windows 包另有双击启动用的 `Start CrewTerm.cmd`。

## 文档

- 用户文档、参考文档与开发文档都在 `docs/` 中，入口是 [文档首页](README.md)。用户可见的变化需要同时更新相关文档与 `CHANGELOG.md`。
- 文档使用中文，与代码注释一致；命令名、参数、字段名与上限必须照抄源码，不确定的内容不写。
- `docs/verification/` 保存各版本开发过程中的验证记录，是历史证据，不随新版本改写。
