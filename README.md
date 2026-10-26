<div align="center">

# CrewTerm

**在一个终端里同时指挥多个 AI 编程 Agent 的跨平台工作台**

[![CI](https://github.com/smallpeanutbaby/CrewTerm/actions/workflows/ci.yml/badge.svg)](https://github.com/smallpeanutbaby/CrewTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/smallpeanutbaby/CrewTerm?include_prereleases&sort=semver)](https://github.com/smallpeanutbaby/CrewTerm/releases)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#平台支持)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

简体中文 · [English](README.en.md)

</div>

CrewTerm 是一个用 C# 编写的终端用户界面（TUI）。它在同一个窗口里并排运行 Claude Code、Codex 等 Agent CLI 或任意 Shell，
旁边是项目文件、代码编辑器、Git 改动和后台任务；并让多个 Agent 之间可以收发消息、交接任务、等待彼此的状态。

每个会话都是一个真实的伪终端进程（Windows 上是 ConPTY，Linux 与 macOS 上是 `forkpty`），由后台服务持有：
关闭界面后 Agent 和任务继续运行，下次打开会连接回原来的会话。

## 目录

- [特性](#特性)
- [平台支持](#平台支持)
- [安装](#安装)
- [快速开始](#快速开始)
- [快捷键](#快捷键)
- [命令行](#命令行)
- [数据与配置](#数据与配置)
- [安全模型](#安全模型)
- [架构](#架构)
- [从源码构建](#从源码构建)
- [已知限制](#已知限制)
- [故障排查](#故障排查)
- [文档](#文档)
- [参与贡献](#参与贡献)
- [许可证](#许可证)

## 特性

**终端与布局**

- 内置 Claude Code、Codex、OpenCode、Gemini CLI、GitHub Copilot CLI 与系统 Shell，也可以添加任意 CLI 或 `ssh` 配置。
- 命名标签页与四向嵌套分屏；每个项目保存自己的标签、分屏比例与会话绑定，关闭布局不会结束进程。
- VT 终端仿真：颜色与文字样式、备用屏幕、中文宽字符与常见 emoji、2,000 行滚动历史、SGR / 传统鼠标协议、
  本地选择与复制、从 `file:line` 跳转到代码。

**多 Agent 协作**

- 语义状态：工作中 / 等待输入 / 完成未读 / 空闲 / 已退出 / 未知，每次变化都带来源与原因；普通输出不会被误判为“完成”。
- 定向收件箱、回复关联、提示词投递、任务交接与状态等待；正在等待审批的 Agent 不会被提示词“代答”。
- Claude Code 与 Codex 的生命周期回调只对单次启动生效，从不修改 Provider 的全局配置。
- 原生会话续接：记录 Provider 的精确会话标识，区分恢复、重新开始与遗忘，绝不自动选择“最近一次会话”。

**项目工具**

- 文件浏览、语法高亮编辑器、全文搜索；保存时保留原编码与换行符，磁盘文件已被 Agent 修改时拒绝覆盖。
- 把选中的代码连同路径与行号以 bracketed paste 发给指定 Agent，由你确认后再提交。
- Git 改动、diff、单文件暂存与 worktree 管理（为每个 Agent 隔离出独立的分支目录）。
- 后台任务与顺序流水线、文件检查点（预览后恢复）、带超时与输出上限的本地插件。
- 完整的命令行接口：终端、任务、状态、消息、交接、原生会话与插件都可以由脚本调用。

## 平台支持

| 平台 | 架构 | 终端后端 | 持续集成 |
| --- | --- | --- | --- |
| Windows 10 1809+ / Windows 11 | x64、arm64 | ConPTY + 作业对象 | `windows-latest` |
| Linux（glibc） | x64、arm64 | `forkpty` + 进程组 | `ubuntu-latest` |
| macOS | Apple Silicon、Intel | `forkpty` + 进程组 | `macos-latest`（Apple Silicon） |

每次推送都会在以上三个系统上构建并运行全部测试。平台专有的行为（例如 Windows 的 `.cmd` 启动脚本规则、Unix 的执行权限与信号）
各自有只在对应平台上运行的测试。

## 安装

### 下载发布包

从 [Releases](https://github.com/smallpeanutbaby/CrewTerm/releases) 下载对应平台的压缩包。发布包自带 .NET 运行时，无需另外安装。

| 系统 | 文件 | 启动 |
| --- | --- | --- |
| Windows | `crewterm-<版本>-win-x64.zip` | 解压后运行 `Start CrewTerm.cmd` 或 `crewterm.exe` |
| Linux | `crewterm-<版本>-linux-x64.tar.gz` | `mkdir -p ~/.local/crewterm && tar -xzf crewterm-*.tar.gz -C ~/.local/crewterm` 后运行 `~/.local/crewterm/crewterm` |
| macOS | `crewterm-<版本>-osx-arm64.tar.gz` | 同 Linux；首次运行前执行 `xattr -dr com.apple.quarantine ~/.local/crewterm` |

把解压目录加入 `PATH` 后即可在任意位置使用 `crewterm` 命令。发布包没有代码签名，Windows 首次运行可能出现 SmartScreen 提示，
macOS 需要按上表移除隔离属性。

### 从源码运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)：

```sh
git clone https://github.com/smallpeanutbaby/CrewTerm.git
cd CrewTerm
dotnet run --project src/CrewTerm.App -c Release -- --project /path/to/your/project
```

### 前置条件

- 推荐使用支持真彩色的现代终端（Windows Terminal、iTerm2、GNOME Terminal、WezTerm 等），窗口至少 120 列 × 35 行，
  并使用支持中文的等宽字体。
- 使用 Git 功能时需要安装 Git。
- 各 Agent CLI 需要自行安装并登录；CrewTerm 不安装、不登录，也不保存任何模型 API Key。

> **从 0.1 升级**：新版本不会自动重启正在运行的旧版后台服务，而是显示 “OLD SERVER” 提示并保留旧终端。
> 保存旧终端中的工作后运行 `crewterm stop`，再重新打开。

## 快速开始

```sh
crewterm --project ~/work/my-app
```

1. 按 **F2** 添加更多本地项目。
2. 按 **F3** 启动一个已安装的 Agent（列表显示实际找到的程序路径），或输入自定义命令，例如 `bash`、`pwsh`、`ssh host`。
3. 按 **F9** 打开命令面板：`Terminal: Split right` 等命令向四个方向分屏，`Tab: New` 新建标签，拖动分隔线调整比例。
4. 按 **Alt+4** 浏览文件、**Alt+3** 编辑代码、**F5** 保存；**F6** 查看 Git 改动；**F7** 运行测试或流水线。
5. 在命令面板中用 `Agent: Notifications`、`Agent: Inbox`、`Agent: Handoff prompt` 管理 Agent 之间的协作。
6. 按 **F10** 离开界面，会话与任务继续在后台运行；再次运行 `crewterm` 即可回到原来的布局。

多个 Agent 在同一目录中会看到彼此的修改。需要隔离时，用 **F6 → Worktrees: create isolated branch** 为每个 Agent 创建独立的 worktree。

## 快捷键

| 按键 | 功能 |
| --- | --- |
| F1 | 帮助 |
| F2 / F3 | 添加项目 / 新建 Agent 或终端 |
| F4 / F5 | 显示或隐藏文件区 / 保存文件 |
| F6 / F7 | Git / 后台任务与流水线 |
| F8 / F9 | 快速切换分屏 / 命令面板（可搜索） |
| F10 | 离开界面，保留后台会话与任务 |
| Alt+1 … Alt+5 | 聚焦第一、第二个终端面板 / 编辑器 / 文件列表 / Agent 列表 |
| Ctrl+Shift+P | 命令面板 |
| Ctrl+Shift+V / Ctrl+Shift+C | 粘贴到终端 / 复制选区（无选区时复制当前屏幕） |
| Shift + 鼠标、滚轮 | 子程序开启鼠标跟踪时仍进行本地选择、菜单或历史滚动 |

命令面板和所有选择器都可以直接输入文字过滤。终端拥有普通按键、Tab、Ctrl+C 与 Esc；F1–F10 由工作台保留，
需要发给子程序时使用 `Terminal: Send reserved key`。

> **macOS**：在 Terminal.app 中开启“将 Option 键用作 Meta 键”（iTerm2 为 “Left Option key: Esc+”）后 Alt 组合键才可用；
> 功能键可能需要同时按住 Fn。

## 命令行

所有子命令连接同一个后台服务，输出 JSON（`read` 输出屏幕文本），成功返回 0，失败时在标准错误中说明原因并返回 1。

```sh
crewterm sessions                                     # 列出运行中的终端
crewterm new --command "claude" --project . --name "Reviewer"
crewterm send <ID> --text $'npm test\r'              # 写入原始输入（\r 即回车）
crewterm read <ID>                                    # 读取屏幕文本
crewterm run --command "dotnet test" --project .      # 后台任务；tasks / task <ID> / cancel <ID>
crewterm agent-state <ID>                             # 语义状态与依据
crewterm agent-wait <ID> --state done,blocked --timeout-ms 30000
crewterm message-send --from <A> --to <B> --text "请审查这次改动"
crewterm plugin-install --path ./my-plugin            # 默认停用，plugin-enable 后才运行
crewterm stop                                         # 停止后台服务及其全部终端与任务
```

参数、退出码、脚本示例与排查方法见 [CLI 使用说明](docs/cli-guide.md)。

## 数据与配置

状态目录保存项目列表、布局与设置、Agent 配置、命令与流水线模板、原生会话目录、Agent 状态与通知、收件箱、插件登记、
任务历史和文件检查点。默认位置：

| 系统 | 默认状态目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\CrewTerm` |
| Linux | `$XDG_DATA_HOME/CrewTerm`，未设置时为 `~/.local/share/CrewTerm` |
| macOS | `~/Library/Application Support/CrewTerm` |

用 `--state-dir PATH` 可以得到一套完全独立的工作台与后台服务。所有状态文件都以“临时文件 → 刷盘 → 原子替换”写入；
损坏的文件会被保留而不是覆盖；多个界面同时保存时报告冲突，不会静默覆盖。

| 操作 | 影响 |
| --- | --- |
| F10 / 关闭界面 | 后台会话与任务继续运行 |
| `Agent: Terminate selected session` / `crewterm close <ID>` | 结束该终端及其全部子进程 |
| `Workspace: Stop all` / `crewterm stop` | 停止后台服务及其全部终端与任务 |
| 重启电脑 | 配置、目录与历史保留；进程无法恢复，可以用 `Agent: Resume original conversation` 续接已记录的原生对话 |

## 安全模型

- **只允许当前用户连接后台服务**：Windows 命名管道与 Unix 域套接字都限制为当前用户，客户端会校验服务端身份；
  Linux / macOS 上新建的状态目录权限为 `0700`。
- **不执行项目中的同名程序**：命令开头的裸程序名只在 `PATH` 的绝对路径条目中查找，不会查找工作目录，
  防止不受信任的仓库劫持 `git`、`node`、`ssh` 等。运行项目内的脚本请写 `.\build.cmd` 或 `./build.sh`。
- **输入不能逃逸**：粘贴、代码上下文与提示词在写入终端前去掉 `ESC` 等控制字符，无法提前结束 bracketed paste 或注入按键。
- **显示不能注入**：会话名、消息与插件名称中的控制字符在服务入口被拒绝；命令与插件输出在显示前清理终端转义序列。
- **插件没有沙箱**：本地插件以你的用户身份运行，安装后默认停用，只安装你信任的插件。
- CrewTerm 不保存模型 API Key，不推算 Token 或费用，不绕过任何 Agent 的审批。

发现安全问题请按 [安全策略](SECURITY.md) 私下报告。

## 架构

```
CrewTerm.App ──► CrewTerm.Terminal     伪终端（ConPTY / forkpty）与 VT 屏幕
     │      ──► CrewTerm.Workspace    文件、编辑、Git、检查点 ──────────────┐
     │      ──► CrewTerm.Automation   任务、Agent 状态与协作、会话、插件 ──┤
     └──────► CrewTerm.Core           路径、原子读写、状态文件、设置与布局模型 ◄┘
```

依赖只能向下：Core 不依赖任何项目，Terminal、Workspace、Automation 互不依赖，只有 App 组合全部模块。
App 内部 `UI`、`Cli` → `Client` → `Protocol` ← `Server`，后台服务不引用界面代码。进程与线程模型、持久化策略、
跨平台设计与审查结论见 [架构文档](docs/architecture.md)。

```
src/
├─ CrewTerm.Core/         IO/  Projects/  Settings/  Layouts/  Editing/  Security/
├─ CrewTerm.Terminal/     Pty/  Emulation/  Native/
├─ CrewTerm.Workspace/    Files/  Documents/  Git/  Checkpoints/
├─ CrewTerm.Automation/   Tasks/  Pipelines/  Agents/  Activity/  Hooks/  Providers/  Plugins/
└─ CrewTerm.App/          Cli/  Client/  Protocol/  Server/  UI/
tests/                    与 src 一一对应，另有共享测试支持 CrewTerm.Testing
```

## 从源码构建

```sh
dotnet build CrewTerm.slnx -c Release       # 警告即错误
dotnet test  CrewTerm.slnx -c Release       # 全部测试
./scripts/publish.sh                        # Linux / macOS：artifacts/<RID>，自带运行时
./scripts/publish.ps1 -Runtime win-x64      # Windows（或任意平台的 PowerShell 7）
```

- 构建强制执行工程规范：缺少 XML 文档注释、注释引用错误、未使用的 `using`、命名空间与文件夹不一致都会使构建失败。
- 测试启动真实的伪终端、Shell、Git 仓库、命名管道后台服务和完整的界面进程；需要 PowerShell 的夹具在 Linux / macOS 上使用 `pwsh`。
- 设置环境变量 `CREWTERM_TEST_EXE` 指向发布包中的可执行文件，可以让界面与 CLI 测试直接验证发布产物。
- 正在运行的后台服务会锁定 `bin/Debug` 下的程序集，开发时请使用 `-c Release` 构建，或先运行 `crewterm stop`。

提交前运行 `./scripts/verify.sh`（或 `./scripts/verify.ps1`）完成构建、测试与空白字符检查。

## 已知限制

- 终端仿真是经过测试的 VT 子集：不支持图像协议、像素级鼠标与 Kitty 键盘增强；滚动历史为纯文本，不保留颜色。
- 多行提示词只在目标 Agent 启用 bracketed paste 时投递；协作需要明确指定收件人与目标状态，不会自动分工或隐式广播。
- 编辑文件上限 4 MiB；全文搜索最多 500 条结果；检查点最多 4,000 个文件 / 64 MiB，恢复逐文件进行，不是跨文件原子事务。
- 每个后台服务最多 32 个终端会话；任务输出保留最后 256K 字符，历史保留最近 100 条。
- 结束会话时会结束整个进程树（Windows 作业对象 / Unix 进程组）；在 Unix 上自行创建新会话的后代进程（例如 `setsid` 启动的守护进程）不在此范围内。
- 没有插件市场、多端同步或远程机器管理；可以在终端中运行 `ssh`，但文件区与后台服务只管理本机。

## 故障排查

| 现象 | 处理 |
| --- | --- |
| F3 显示 “not installed” | 运行 `crewterm doctor` 查看实际查找结果，安装 CLI 或修正 `PATH` 后重试 |
| 界面显示 “OLD SERVER” | 旧版后台服务仍在运行；保存工作后执行 `crewterm stop`，再重新打开 |
| 后台服务无法启动 | 查看状态目录中的 `server-error.log` |
| 项目中的脚本无法启动 | 裸程序名不会在项目目录中查找；写成 `./script.sh`、`.\script.cmd` 或绝对路径；Linux / macOS 上还需要执行权限 |
| Alt 组合键无效（macOS） | 在终端设置中把 Option 键用作 Meta 键 |
| 原生会话无法恢复 | 在会话列表中查看原因，用 `Agent: Bind native conversation / resume command` 登记精确标识或恢复命令 |
| 插件没有运行 | 确认已启用（`crewterm plugins`），用 `crewterm plugin-history` 查看错误与退出码 |

## 文档

| 文档 | 内容 |
| --- | --- |
| [架构](docs/architecture.md) | 分层与依赖规则、工程规范、跨平台设计、进程与线程模型、持久化、审查结论 |
| [CLI 使用说明](docs/cli-guide.md) | 全部命令、参数、退出码与脚本示例 |
| [原生会话](docs/native-sessions.md) | 原生会话的记录、恢复与重新开始 |
| [本地插件](docs/local-plugins.md) | 插件清单格式、事件与运行规则 |
| [插件示例](examples/plugins/workspace-notes/README.md) | 可以直接运行的插件 |
| [更新日志](CHANGELOG.md) | 各版本的变更 |

`docs/verification-*.md` 与 `docs/v02-features.md` 是各版本开发过程中的验证记录。

## 参与贡献

欢迎提交问题与改进。开始之前请阅读 [贡献指南](CONTRIBUTING.md)，了解开发环境、工程规范与提交要求。

## 许可证

CrewTerm 以 [MIT 许可证](LICENSE) 发布。
