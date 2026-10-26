# CrewTerm

**用 C# 编写的 Windows 原生终端多 Agent 工作台。** 在同一个终端界面里同时运行 Claude Code、Codex、其他 Agent CLI 或 Shell，
并在旁边浏览项目、编辑代码、审查 Git 改动、运行测试与流水线、管理多个 Agent 之间的协作。

CrewTerm 使用真实的 Windows ConPTY 进程：每个会话拥有独立的进程树、工作目录、输入和屏幕。会话由后台服务（daemon）持有，
按 F10 离开界面或关闭窗口后，Agent 与任务继续运行；再次打开 CrewTerm 会连接回原来的服务。

当前版本：**0.2.0**（Windows x64）。

---

## 目录

1. [能否正式使用](#能否正式使用)
2. [系统要求](#系统要求)
3. [安装与启动](#安装与启动)
4. [快速上手](#快速上手)
5. [快捷键](#快捷键)
6. [功能一览](#功能一览)
7. [命令行](#命令行)
8. [会话、数据与安全](#会话数据与安全)
9. [架构与项目结构](#架构与项目结构)
10. [开发、测试与发布](#开发测试与发布)
11. [当前边界](#当前边界)
12. [故障排查](#故障排查)
13. [文档索引](#文档索引)

---

## 能否正式使用

**可以在 Windows 10 1809+ / Windows 11 x64 上作为日常工具正式使用**，前提是理解下面的验证范围：

| 已验证 | 说明 |
| --- | --- |
| 全部自动化测试 | 5 个测试项目共 **493 个测试全部通过**，构建 0 警告 0 错误；测试覆盖真实文件与 Git 仓库、ConPTY 子进程、命名管道 daemon、CLI 进程和整个界面的键盘/鼠标操作 |
| 发布包 | `scripts/publish.ps1` 生成自带运行时的独立包；用发布包中的 `crewterm.exe` 重跑全部 211 个界面/CLI/daemon 测试通过 |
| 真实 Agent CLI | 本机安装的 Claude Code 2.1.x 与 Codex 0.163 通过 CrewTerm 启动、运行、正常退出 |
| 安全审查 | 两轮架构审查加一轮独立复审，修复了命令劫持、粘贴逃逸、终端转义注入等问题，见 [架构文档 §7](docs/architecture.md#7-审查结论) |

| 未覆盖 | 影响 |
| --- | --- |
| 付费模型回合 | 没有发送真实的付费提示；Agent 的认证、计费与模型行为由各 CLI 自己负责 |
| 未安装的 Provider | OpenCode、Gemini、Copilot 只按官方参数验证了命令构造，没有实机运行 |
| 长时间运行 | 没有做多日连续运行的稳定性测试 |
| 代码签名 | 发布包未签名，首次运行可能出现 SmartScreen 提示 |

建议：重要项目先用独立的 `--state-dir` 试用一段时间；需要隔离多个 Agent 的修改时使用 Git worktree（F6）。

## 系统要求

- Windows 10 1809 或更高版本 / Windows 11，x64（依赖 ConPTY）。
- 推荐 Windows Terminal，窗口至少 120 列 × 35 行，使用支持中文的等宽字体。
- 从源码运行需要 .NET 10 SDK；发布包自带运行时，无需安装 .NET。
- Git（使用 Git 功能时）；各 Agent CLI 需自行安装并登录（CrewTerm 不安装、不登录、不保存模型 API Key）。

## 安装与启动

**发布包**：解压后运行 `Start CrewTerm.cmd`，或在终端中：

```powershell
.\crewterm.exe --project "D:\work\my-project"
```

**从源码运行**：

```powershell
git clone https://github.com/smallpeanutbaby/CrewTerm.git
cd CrewTerm
dotnet run --project src/CrewTerm.App -c Release -- --project "D:\work\my-project"
```

源码根目录的 `CrewTerm.cmd` 会优先使用 `artifacts\win-x64` 中的发布包，没有时改用 `dotnet run`，并打开 CrewTerm 仓库本身作为项目。

**生成发布包**（输出到 `artifacts/win-x64`）：

```powershell
.\scripts\publish.ps1
```

常用启动参数：

| 参数 | 作用 |
| --- | --- |
| `--project PATH` | 启动时打开（并登记）该项目 |
| `--state-dir PATH` | 使用独立的状态目录，得到一套互不影响的工作台与后台服务 |
| `--help` / `--version` | 帮助 / 版本 |

> **从 0.1 升级**：新客户端不会自动重启正在运行的旧版后台服务，而是显示 “OLD SERVER” 提示并保留旧终端。
> 请先完成或保存旧终端中的工作，再运行 `crewterm stop` 停止旧服务，然后重新打开新版。

## 快速上手

1. 按 **F2** 添加本地项目目录。
2. 按 **F3** 选择已安装的 Agent（列表显示实际找到的程序路径），或输入自定义命令（例如 `cmd.exe`、`ssh host`）。
3. 按 **F9** 打开命令面板：`Tab: New` 新建标签，`Terminal: Split right / left / down / up` 向四个方向分屏，
   每个面板可以继续分割；拖动分隔线调整比例，点击标签切换。`Terminal: Choose session for active pane` 把已有会话接入当前面板。
4. 按 **Alt+4** 进入文件列表，Enter 打开目录或文件；**Alt+3** 聚焦编辑器，**F5** 保存。
5. 在 F9 中用 `Agent: Notifications` 查看通知，`Agent: Inbox` 读取消息，`Agent: Handoff prompt` 向指定 Agent 交接任务，
   `Agent: Wait for selected state` 等待某个状态。
6. 按 **F10** 分离界面，后台会话与任务继续运行。关闭标签或面板只改变布局；终止进程需要明确选择 `Agent: Terminate selected session`。

同一目录中的多个 Agent 会看到彼此的修改。需要隔离时，用 **F6 → Worktrees: create isolated branch** 创建 Git worktree，再把 Agent 启动到各自的目录。

## 快捷键

| 按键 | 功能 |
| --- | --- |
| F1 | 帮助 |
| F2 / F3 | 添加项目 / 新建 Agent 或终端 |
| F4 / F5 | 显示或隐藏文件区 / 保存文件 |
| F6 / F7 | Git 操作 / 后台任务与流水线 |
| F8 / F9 | 快速切换分屏 / 可搜索的命令面板 |
| F10 | 分离界面，保留后台会话与任务 |
| Alt+1 / Alt+2 | 聚焦第一 / 第二个终端面板（更多面板用 `Terminal: Focus next pane`） |
| Alt+3 / Alt+4 / Alt+5 | 聚焦编辑器 / 文件列表 / Agent 列表 |
| Ctrl+Shift+P | 命令面板 |
| Ctrl+Shift+V / Ctrl+Shift+C | 粘贴到终端 / 复制选区（无选区时复制当前屏幕） |
| 鼠标拖选 / 右键 | 选择并复制文本 / 终端操作菜单 |
| Shift + 鼠标、滚轮 | 在子程序开启鼠标跟踪时仍进行本地选择、菜单或历史滚动 |
| Esc | 关闭弹窗；在终端中传给子程序 |

命令面板和所有选择器都可以直接输入文字过滤，↓ 进入列表，Enter 选择。终端拥有普通按键、Tab、Ctrl+C、Esc；
F1–F10 被工作台保留，需要发给子程序时使用 F9 的 `Terminal: Send reserved key`。

## 功能一览

- **多项目与多 Agent**：内置 Claude Code、Codex、OpenCode、Gemini、Copilot、Shell，可添加自定义 CLI / SSH 配置。
  每个项目保存命名标签、四向嵌套分屏、分隔比例和会话绑定；关闭布局不会结束进程。
- **语义状态与通知**：工作中 / 等待输入 / 完成未读 / 已读空闲 / 已退出 / 未知，每次变化都带来源、原因和版本号。
  普通输出或进程存活不会被当作“完成”；只识别明确的审批提示和 Provider hook 事件。
- **Agent 协作**：定向收件箱、回复关联、显式确认、提示词投递、任务交接与状态等待。消息入箱和写入终端是两个独立动作；
  正在等待审批的 Agent 不会被提示词“代答”。
- **原生会话续接**：持久保存 Provider 与精确的原生会话 ID，区分关闭、恢复、重新开始与遗忘；恢复保留已验证的启动参数，
  从不自动选择“最近一次会话”。见 [原生会话说明](docs/native-sessions.md)。
- **终端仿真**：VT 颜色与文字样式、光标、备用屏幕、中文宽字符与常见 emoji 组合、2,000 行滚动历史、查询回复、窗口缩放；
  SGR / 传统鼠标协议、本地文本选择与自动复制、右键菜单、从选中的 `file:line` 跳转到代码。
- **代码编辑**：目录浏览、语法高亮、行号、撤销 / 重做、查找、跳转行、项目全文搜索；保存时保持原编码与换行符，
  磁盘文件已被 Agent 修改时拒绝覆盖。
- **代码上下文**：把选中的代码连同文件路径与行号插入指定 Agent 的输入区（bracketed paste），由你确认后再提交。
- **Git**：工作区与暂存区改动、文件 diff、单文件暂存、worktree 列表 / 创建 / 打开 / 删除（拒绝删除有改动的 worktree）。
- **后台任务与流水线**：运行任意构建或测试命令，实时输出、真实退出码、取消、历史；命令模板；顺序流水线，任一步失败即停止；
  并发数默认 4，可设置为 1–32。
- **文件检查点**：捕获项目文件、预览差异、确认后恢复；预览之后文件又被修改时拒绝恢复。
- **本地插件**：从本地目录登记 JSON 清单（默认停用），以真实进程和 JSON 标准输入运行命令与事件钩子，带超时与输出上限。
  见 [插件说明](docs/local-plugins.md) 与 [可运行示例](examples/plugins/workspace-notes/README.md)。
- **命令行接口**：终端、任务、语义状态、通知、收件箱、交接、等待、原生会话、插件都可脚本调用。

## 命令行

所有子命令都连接同一个后台服务，输出 JSON（`read` 输出屏幕文本），成功返回 0，失败在标准错误中说明原因并返回 1。

```powershell
crewterm sessions                                   # 列出运行中的终端
crewterm new --command "claude" --project D:\work\app --name "Reviewer"
crewterm send ID --text "npm test`r"                # 写入原始输入
crewterm read ID                                    # 读取屏幕文本
crewterm run --command "dotnet test" --project .    # 后台任务；task ID / cancel ID / tasks
crewterm agent-state ID                             # 语义状态与依据
crewterm agent-wait ID --state done,blocked --timeout-ms 30000
crewterm message-send --from A --to B --text "请审查 PR"
crewterm plugin-install --path .\my-plugin          # 默认停用，plugin-enable 后才运行
crewterm stop                                       # 停止后台服务、所有终端与任务
```

完整说明（参数、退出码、脚本示例、排查）见 [CLI 使用说明](docs/cli-guide.md)。相对路径按执行命令时的当前目录解析。

## 会话、数据与安全

**状态目录**默认是 `%LOCALAPPDATA%\CrewTerm`，用 `--state-dir` 可以创建互相独立的工作台。目录中保存项目列表、布局与设置、
Agent 配置、命令与流水线模板、原生会话目录、语义状态与通知、收件箱、插件登记、任务历史和文件检查点。
所有状态文件都以“临时文件 → 刷盘 → 原子替换”写入，损坏的文件会被保留而不是覆盖；多个界面同时保存时报告冲突，不会静默覆盖。

| 操作 | 影响 |
| --- | --- |
| F10 / 关闭界面 | 后台会话与任务继续运行 |
| `Agent: Terminate selected session` / `close ID` | 结束该终端及其全部子进程 |
| `cancel ID` | 取消一个后台任务 |
| `Workspace: Stop all` / `stop` | 停止后台服务及其全部终端与任务 |
| 电脑或服务重启 | 配置、目录和历史保留；进程内存无法恢复，可通过 `Agent: Resume original conversation` 续接已登记的原生对话 |
| `session-forget ID` | 遗忘已关闭的会话记录；Provider 自己的对话历史不删除 |

**安全模型**：

- 后台服务的命名管道只允许当前 Windows 用户连接，客户端会校验服务端属于同一用户。
- 终端命令开头的裸程序名只在 `PATH` 中查找，**不会**执行项目目录中的同名程序；会话中的 cmd.exe 同样不从当前目录查找程序
  （防止不受信任的仓库劫持 `cmd`、`ssh`、`node` 等）。运行项目内的脚本请写 `.\script.cmd`。
- 粘贴、代码上下文与提示词在写入终端前去掉 `ESC` 等控制字符，无法提前结束 bracketed paste 或注入按键。
- 会话名、状态原因、消息、插件名称中的控制字符在服务入口被拒绝；命令与插件输出在界面显示前清理 ANSI 序列，
  不会把控制序列送到你的真实终端。
- 本地插件以你的 Windows 用户身份运行，**没有沙箱**；只安装你信任的插件，安装后默认停用。
- CrewTerm 不保存模型 API Key，不推算 Token 或费用，不绕过任何 Agent 的审批。

## 架构与项目结构

```
CrewTerm.App ──► CrewTerm.Terminal     ConPTY 与 VT 屏幕（不依赖其他项目）
     │      ──► CrewTerm.Workspace    文件、编辑、Git、检查点 ──► Core
     │      ──► CrewTerm.Automation   任务、Agent 状态与协作、原生会话、插件 ──► Core
     └──────► CrewTerm.Core           路径规则、原子读写、状态文件、项目/设置/布局模型、安全文本处理
```

```
src/
├─ CrewTerm.Core/         IO/  Projects/  Settings/  Layouts/  Editing/  Security/
├─ CrewTerm.Terminal/     Pty/  Emulation/  Native/
├─ CrewTerm.Workspace/    Files/  Documents/  Git/  Checkpoints/
├─ CrewTerm.Automation/   Tasks/  Pipelines/  Agents/  Activity/  Hooks/  Providers/  Plugins/
└─ CrewTerm.App/          Cli/  Client/  Protocol/  Server/  UI/Windows/  UI/Controls/  UI/Input/
tests/                    与 src 一一对应（另有 App.Tests/Tui 端到端界面测试与各项目的 Fixtures/）
examples/plugins/         可运行的本地插件示例
scripts/publish.ps1       生成自带运行时的 win-x64 发布包
```

- 依赖只能向下：Core 不依赖任何项目；Terminal、Workspace、Automation 互不依赖；只有 App 组合全部模块。
  App 内部 `UI`、`Cli` → `Client` → `Protocol` ← `Server`，后台服务不引用界面或客户端代码。
- 一个文件只声明一个类型，命名空间与文件夹一致；所有类型与成员（含私有）都有中文 XML 文档注释。

进程与线程模型、持久化策略、注释规范和两轮审查的完整结论见 [架构文档](docs/architecture.md)。

## 开发、测试与发布

```powershell
dotnet build CrewTerm.slnx -c Release      # 0 警告 0 错误
dotnet test  CrewTerm.slnx -c Release      # 493 个测试
.\scripts\publish.ps1                      # artifacts/win-x64，自带 .NET 运行时
```

- 构建强制执行工程规范（`Directory.Build.props` + `.editorconfig`）：警告即错误；缺少 XML 注释、注释格式或引用错误、
  未使用的 `using`、命名空间与文件夹不一致都会使构建失败。
- 测试中需要真实 PowerShell 进程的夹具优先使用 PowerShell 7，没有时自动使用系统自带的 Windows PowerShell，无需额外安装。
- 设置环境变量 `CREWTERM_TEST_EXE` 指向发布包中的 `crewterm.exe`，可以让界面与 CLI 测试直接验证发布产物。
- 正在使用默认状态目录的 CrewTerm 后台服务会锁定 `bin\Debug` 下的程序集；开发时请用 `-c Release` 构建，或先 `crewterm stop`。

## 当前边界

- 只支持 **Windows x64**；没有 Linux / macOS 的 PTY 后端。
- 不包含插件市场、多端同步或多机器管理；自定义命令可以运行已安装的 `ssh`，但文件区与后台服务以本机为范围。
- 协作需要由用户或调用 API 的本地程序明确指定收件人、消息和目标状态；不自动分工、不隐式广播、不替你合并代码。
- 多行提示词只在目标 Agent 启用 bracketed paste 时投递。
- 终端是经过测试的 VT 子集：不支持图像协议、像素级鼠标与 Kitty 键盘增强；滚动历史为纯文本，不保留颜色；单个字形簇最多 64 个 UTF-16 单元。
- 编辑文件上限 4 MiB；全文搜索最多 500 条；检查点最多 4,000 个文件 / 64 MiB，恢复逐文件检查但不是跨文件原子事务，恢复前请停止正在修改这些文件的 Agent。
- 每个后台服务最多 32 个终端会话；任务输出保留最后 256K 字符，历史保留最近 100 条。状态目录可能包含命令输出和源码检查点，请按项目需要管理。

## 故障排查

| 现象 | 处理 |
| --- | --- |
| F3 显示 “not installed” | 运行 `crewterm doctor` 查看实际查找结果，安装 CLI 或修正 PATH 后重试 |
| 界面显示 “OLD SERVER” | 旧版后台服务仍在运行；保存工作后执行 `crewterm stop`，再重新打开 |
| 后台服务无法启动 | 查看状态目录中的 `server-error.log` |
| 项目里的脚本无法作为命令启动 | 裸程序名不会从项目目录查找，写成 `.\script.cmd` 或绝对路径 |
| 提示 “Settings changed in another client” | 另一个界面刚保存过设置；重新打开后再修改 |
| 保存文件提示冲突 | 文件已被 Agent 修改；用 `Editor: Reload file from disk` 查看后再编辑 |
| 原生会话无法恢复 | 在会话列表中查看 `ResumeReason`；用 `Agent: Bind native conversation / resume command` 登记精确 ID 或恢复命令 |
| 插件没有运行 | 确认已启用（`plugins`），在 `plugin-history` 查看错误与退出码 |

更多排查见 [CLI 使用说明](docs/cli-guide.md) 末尾。

## 文档索引

| 文档 | 内容 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 分层与依赖规则、工程规范、项目结构、进程与线程模型、持久化、注释规范、审查结论 |
| [docs/cli-guide.md](docs/cli-guide.md) | 命令行完整说明与脚本示例 |
| [docs/native-sessions.md](docs/native-sessions.md) | 原生会话的记录、恢复与重新开始 |
| [docs/local-plugins.md](docs/local-plugins.md) | 本地插件清单格式、事件与运行规则 |
| [docs/v02-features.md](docs/v02-features.md) | 0.2 功能验收矩阵与验证边界 |
| `docs/verification-*.md` | 各模块开发过程中的验证记录（历史证据） |
| [examples/plugins/workspace-notes](examples/plugins/workspace-notes/README.md) | 可直接运行的插件示例 |
