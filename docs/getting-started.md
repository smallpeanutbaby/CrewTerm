# 快速入门

本文带你从安装开始，用大约十分钟完成一次完整的多 Agent 工作流程：打开项目、并排启动两个 Agent、为其中一个隔离出独立的 worktree、
把代码交给 Agent 审查、查看通知，最后离开界面再回来。每一步都只用到已发布的功能；各功能的完整说明见 [使用手册](user-guide.md)。

## 目录

- [1. 安装](#1-安装)
- [2. 检查环境](#2-检查环境)
- [3. 打开项目](#3-打开项目)
- [4. 启动第一个 Agent](#4-启动第一个-agent)
- [5. 分屏并启动第二个 Agent](#5-分屏并启动第二个-agent)
- [6. 为 Agent 隔离出独立的 worktree](#6-为-agent-隔离出独立的-worktree)
- [7. 把代码交给 Agent](#7-把代码交给-agent)
- [8. 查看状态与通知](#8-查看状态与通知)
- [9. 在后台运行测试](#9-在后台运行测试)
- [10. 离开与回来](#10-离开与回来)
- [下一步](#下一步)

## 1. 安装

从 [Releases](https://github.com/smallpeanutbaby/CrewTerm/releases) 下载对应平台的压缩包，发布包自带 .NET 运行时：

| 系统 | 操作 |
| --- | --- |
| Windows | 解压 `crewterm-<版本>-win-x64.zip`，运行 `crewterm.exe`（或双击 `Start CrewTerm.cmd`） |
| Linux | `mkdir -p ~/.local/crewterm && tar -xzf crewterm-*-linux-x64.tar.gz -C ~/.local/crewterm` |
| macOS | 同 Linux（包名为 `osx-arm64` 或 `osx-x64`），首次运行前执行 `xattr -dr com.apple.quarantine ~/.local/crewterm` |

把解压目录加入 `PATH`，之后在任意位置都可以使用 `crewterm`。也可以从源码运行：

```sh
git clone https://github.com/smallpeanutbaby/CrewTerm.git
cd CrewTerm
dotnet run --project src/CrewTerm.App -c Release -- --project /path/to/your/project
```

终端窗口建议至少 120 列 × 35 行，使用支持中文的等宽字体。macOS 上请在终端设置中把 Option 键用作 Meta 键，否则 Alt 组合键不可用。

## 2. 检查环境

```sh
crewterm --version
crewterm doctor
```

`doctor` 列出状态目录，以及 Claude Code、Codex、OpenCode、Gemini CLI、GitHub Copilot CLI 和 Shell 的实际程序路径；
没有找到的显示 `not installed`。它不启动任何 Agent，也不检查登录状态。CrewTerm 不安装、不登录 Agent CLI，
请先按各 CLI 的说明安装并登录，至少准备好一个（本教程以 Claude Code 和 Codex 为例，换成其他 Agent 或普通 Shell 也可以）。

> 第一次试用时，可以加上 `--state-dir ~/crewterm-trial` 使用一套独立的状态目录，后面所有命令都带上同一个参数。
> 试用结束后运行 `crewterm --state-dir ~/crewterm-trial stop` 并删除该目录即可。

## 3. 打开项目

```sh
crewterm --project ~/work/my-app
```

CrewTerm 会登记这个项目并打开工作台，第一次需要时自动在后台启动后台服务（daemon）。左侧侧栏的 `spaces` 列出项目
（名称旁显示 Git 分支），`agents` 列出正在运行的 Agent，`files` 列出当前项目的文件（单击文件在终端下方的代码查看器中打开）。之后要添加更多项目，点击 `spaces` 旁的 **+ new** 或按 **F2**。

界面底部列出常用功能键；按 **F1** 随时查看帮助。也可以像 herdr / tmux 那样使用前缀键：先按 **Ctrl+b**，
再按一个键（例如 `v` 分屏、`c` 新建标签页、`q` 离开界面），按 `Ctrl+b ?` 查看全部。

## 4. 启动第一个 Agent

1. 点击 `agents` 旁的 **+ new**（或按 **F3**），列表中显示每个内置 Agent 以及找到的程序路径，最后一项是 `Custom command…`。
2. 选择 **Claude Code**，回车。Agent 在当前项目目录中启动，显示在当前终端面板里，并出现在侧栏的 `agents` 中。
3. 像在普通终端中一样和它交互。终端拥有普通按键、Tab、Ctrl+C 与 Esc；F1–F10 由工作台保留。

如果要运行列表中没有的程序，选择 `Custom command…`，输入命令（例如 `bash`、`pwsh`、`ssh host`）和显示名称。

## 5. 分屏并启动第二个 Agent

1. 按 **Ctrl+b** 再按 **v**（或按 **F9** 打开命令面板执行 `Terminal: Split right`）。右侧出现一个新面板并获得焦点。
2. 再按 **F3**，选择 **Codex**。新 Agent 显示在右侧面板中。
3. 用鼠标拖动两个面板之间的分隔线调整比例；点击面板或按 **Alt+1** / **Alt+2** 切换焦点。

面板可以继续向四个方向分割（`Ctrl+b -` 向下分屏）；点击标签栏的 `+` 或按 `Ctrl+b c` 新建标签页。
单击 `agents` 中的任意一行，就会把那个 Agent 显示到当前面板。关闭面板或标签页只改变布局，里面的 Agent 继续运行，
可以用 `Terminal: Choose session for active pane` 把它重新放回任意面板。布局按项目保存，下次打开时恢复。

## 6. 为 Agent 隔离出独立的 worktree

两个 Agent 在同一个目录中工作时，会看到并可能覆盖彼此的修改。让其中一个在独立的分支目录中工作：

1. 按 **F6**，选择 `Worktrees: create isolated branch`。
2. 输入新分支名（默认 `crew/feature`）和新目录（默认是项目目录加 `-worktree` 后缀）。
3. CrewTerm 创建 worktree，把它登记为一个新项目并切换过去。在这里按 **F3** 启动的 Agent 只修改这个目录。

完成后，可以在 F6 中用 `Worktrees: remove clean checkout` 删除没有未提交改动的 worktree（有改动时 Git 会拒绝删除）。
合并分支由你自己用 Git 完成，CrewTerm 不会替你合并。

## 7. 把代码交给 Agent

1. 在侧栏最下面的 `files` 中单击目录进入、单击文件，文件在终端下方的代码查看器中打开（**F4** 显示 / 隐藏查看器，Esc 回到终端）。
2. 在编辑器中选中几行代码。
3. 按 **F9**，执行 `Editor: Send selected code to an agent`，输入说明（默认 `Review this code`），再选择目标 Agent。
4. 代码连同文件路径和行号以 bracketed paste 插入目标 Agent 的输入区，**不会自动提交**：切换到那个终端检查内容后再按 Enter。

目标程序必须启用了 bracketed paste（主流 Agent CLI 都会启用），否则 CrewTerm 会拒绝发送，避免代码被当作 Shell 命令执行。

在编辑器中修改文件后按 **F5** 保存。如果 Agent 已经在磁盘上改过这个文件，CrewTerm 会拒绝覆盖并提示冲突。

## 8. 查看状态与通知

侧栏的 `agents`（**Alt+5** 聚焦）显示每个会话的语义状态：`◉ blocked` 等待你决定、`● working` 工作中、
`● done` 完成未读、`○ idle` 空闲，以及 `○ unknown`、`✕ exited`。等待决定的 Agent 排在最前面，其次是完成未读的；
`spaces` 中每个项目的状态点显示其中最需要关注的那一个。右键单击一行可以直接解释状态、发消息、交接或终止。

- `Agent: Explain selected status`：查看当前状态的来源和原因。
- `Agent: Notifications`：查看完成与等待输入的通知；`Agent: Mark selected as read` 确认已读。
- `Agent: Send message` / `Agent: Inbox`：在 Agent 之间收发定向消息；`Agent: Handoff prompt`：把任务交给另一个 Agent。

Claude Code 与 Codex 的状态来自它们的生命周期回调，只对 CrewTerm 启动的这一次生效，不修改它们的全局配置。
其他 Agent 在没有明确依据时显示为“未知”——CrewTerm 不会把普通输出当作“完成”。详见 [Agent 集成指南](agent-integration.md)。

## 9. 在后台运行测试

按 **F7**，选择 `Run command`，输入 `dotnet test`、`npm test` 等命令（默认 `dotnet test`，在当前项目根目录中运行）。
任务在后台服务中运行，显示实时输出和真实退出码，不占用任何终端面板；`Live tasks / history / cancel` 查看或取消任务。
常用命令可以用 `Save command template` 保存为模板，多个步骤可以用 `Create pipeline template` 组成顺序流水线，任一步失败即停止。

## 10. 离开与回来

按 **F10** 离开界面。Agent、终端和后台任务都继续在后台服务中运行。在任意终端中再次运行：

```sh
crewterm
```

工作台连接回原来的后台服务，恢复标签页、分屏和会话。不打开界面也可以用命令行查看和控制：

```sh
crewterm sessions                 # 列出会话（JSON）
crewterm read <会话ID>            # 读取某个终端的屏幕文本
crewterm agent-state <会话ID>     # 查看语义状态
```

要结束全部会话与任务，在命令面板中执行 `Workspace: Stop all sessions and background tasks`，或运行 `crewterm stop`。
重启电脑后进程无法保留，但 Claude Code、Codex 等已记录精确会话标识的对话可以用 `Agent: Resume original conversation` 续接，
见 [原生会话](native-sessions.md)。

## 下一步

| 想要 | 阅读 |
| --- | --- |
| 了解界面的每一个功能 | [使用手册](user-guide.md) |
| 查找快捷键或命令面板中的命令 | [快捷键与命令参考](commands.md) |
| 用脚本编排多个 Agent | [CLI 使用说明](cli-guide.md)、[多 Agent 工作流实战](workflows.md) |
| 了解各 Agent 的状态检测与接入方式 | [Agent 集成指南](agent-integration.md) |
| 修改状态目录、自定义 Agent 或模板 | [配置与数据参考](configuration.md) |
| 编写插件 | [本地插件](local-plugins.md) |
| 遇到问题 | [常见问题](faq.md)、README 的故障排查 |
