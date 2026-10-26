# 常见问题

本文按主题回答使用 CrewTerm 时最常见的问题。具体的错误现象与处理办法见 README 的[故障排查](../README.md#故障排查)
与 [CLI 使用说明](cli-guide.md#排查问题)。

## 目录

- [基本概念](#基本概念)
- [Agent 与状态](#agent-与状态)
- [会话与数据](#会话与数据)
- [终端与按键](#终端与按键)
- [安全与隐私](#安全与隐私)
- [与其他工具的比较](#与其他工具的比较)
- [参与项目](#参与项目)

## 基本概念

### CrewTerm 适合什么场景？

同时使用多个 AI 编程 Agent（Claude Code、Codex 等），并且希望在同一个终端窗口里看到它们的状态、在旁边查看和编辑代码、
审查 Git 改动、运行测试，还需要在 Agent 之间传递消息或交接任务。只运行一个 Agent 时，CrewTerm 依然可以作为带文件区与 Git 的终端工作台使用。

### CrewTerm 会替我拆分任务、自动协调多个 Agent 吗？

不会。CrewTerm 提供协作的“管道”——定向消息、提示投递、交接和状态等待，但收件人、内容和等待条件都由你或你的脚本明确指定。
它不自动分工、不隐式广播、不替你合并代码。用脚本编排的方法见 [多 Agent 工作流实战](workflows.md)。

### 需要付费吗？CrewTerm 会调用模型吗？

CrewTerm 以 MIT 许可证免费发布，本身不调用任何模型。各 Agent CLI 的认证、计费与模型行为由它们自己负责；
CrewTerm 不保存 API Key，也不推算 Token 或费用。

### 支持哪些平台？

Windows 10 1809+ / Windows 11（x64、arm64）、Linux（glibc，x64、arm64）与 macOS（Apple Silicon、Intel）。
每次推送都在 Windows、Ubuntu、macOS 上构建并运行全部测试。

## Agent 与状态

### 支持哪些 Agent？列表里没有的能用吗？

内置 Claude Code、Codex、OpenCode、Gemini CLI、GitHub Copilot CLI 与系统 Shell。任何交互式命令行程序都可以通过
F3 的 `Custom command…` 运行，也可以用 `Agent: Save custom CLI / SSH profile` 保存为常用配置。
不同 Agent 的接入深度不同（状态检测、原生会话恢复），对照表见 [Agent 集成指南](agent-integration.md)。

### 为什么某个 Agent 的状态一直是“未知”？

CrewTerm 只根据明确的依据判断状态：Provider 的生命周期回调（Claude Code 的 hook、Codex 的 notify）、明确的审批提示，
以及通过 `crewterm agent-report` 显式上报的状态。普通输出或进程仍在运行都不足以说明 Agent “完成”了，此时显示为未知。
用 `Agent: Explain selected status` 可以看到状态的来源与原因；如果 Claude Code 或 Codex 的自动接入因为你已有的配置被跳过，原因会写在这里。

### 自动接入会修改 Claude Code 或 Codex 的配置吗？

不会。接入参数只附加在 CrewTerm 这一次启动的命令行上（Claude Code 的调用级 `--settings`、Codex 的调用级 `notify`），
不写入它们的全局配置。命令行中已经有 `--settings`、`--bare`、`--safe-mode` 或 notify 配置时，CrewTerm 保留你的设置并跳过自动接入。

### 投递提示词后，能确定 Agent 已经执行了吗？

不能。投递成功只说明文字已经写入终端。CrewTerm 也不会向正在等待审批的 Agent 投递提示词，避免“代答”审批。
需要确认结果时，等待它进入 `done` 状态并检查其输出。

## 会话与数据

### 关闭窗口或断开 SSH，Agent 会停吗？

不会。会话由后台服务持有，界面只是客户端。按 F10 离开、关闭终端窗口或断开 SSH 后，Agent 与后台任务继续运行；
再次运行 `crewterm` 即可连接回去。只有 `Agent: Terminate selected session`、`crewterm close`、`Workspace: Stop all ...`
或 `crewterm stop` 会结束进程。

### 重启电脑后会怎样？

配置、布局、任务历史、收件箱与原生会话目录都保留在状态目录中，但进程无法跨重启保留。对于已经记录了精确会话标识的 Agent，
可以用 `Agent: Resume original conversation`（或 `crewterm session-resume`）续接原来的对话。CrewTerm 从不自动选择“最近一次会话”，
详见 [原生会话](native-sessions.md)。

### 能在远程服务器上使用吗？

可以在远程 Linux / macOS 主机上通过 SSH 运行 `crewterm`：后台服务脱离启动它的终端，SSH 断开后会话继续运行，重新登录后再次运行 `crewterm` 即可。
CrewTerm 不提供“在一个窗口中管理多台机器”的功能；在本机终端里运行 `ssh` 也可以，但文件区与后台服务只管理所在的那台机器。

### 多个 Agent 同时修改同一个项目怎么办？

在同一个目录中工作的 Agent 会看到彼此的修改。需要隔离时，用 F6 的 `Worktrees: create isolated branch` 为每个 Agent 创建独立的 Git worktree。
编辑器保存时如果发现文件已被 Agent 改过，会拒绝覆盖；文件检查点可以在恢复前预览差异。

### 数据保存在哪里？如何备份或重置？

默认状态目录：Windows 为 `%LOCALAPPDATA%\CrewTerm`，Linux 为 `$XDG_DATA_HOME/CrewTerm`（默认 `~/.local/share/CrewTerm`），
macOS 为 `~/Library/Application Support/CrewTerm`。先运行 `crewterm stop`，再复制或删除这个目录即可备份或重置。
每个文件的用途见 [配置与数据参考](configuration.md)。

### 能同时运行两套互不影响的工作台吗？

可以。用 `--state-dir PATH` 指定不同的状态目录，每个目录拥有自己的后台服务、会话、设置和历史。之后的 CLI 命令要带上同一个 `--state-dir`。

### 升级后界面显示 “OLD SERVER”？

新版本不会自动停止正在运行的旧版后台服务，以免中断你的工作。保存旧终端中的工作后运行 `crewterm stop`，再重新打开 CrewTerm。

## 终端与按键

### 为什么 F1–F10 不能发给终端里的程序？

这些键由工作台使用。需要发给子程序时，在命令面板中执行 `Terminal: Send reserved key (F1–F12 / Tab / Esc)`。

### macOS 上 Alt 组合键或功能键没有反应？

在 Terminal.app 中开启“将 Option 键用作 Meta 键”（iTerm2 为 “Left Option key: Esc+”）；功能键可能需要同时按住 Fn。

### 终端程序开启了鼠标跟踪，我还能选中文字吗？

可以。按住 Shift 再用鼠标拖选、右键或滚动，CrewTerm 会在本地处理，而不是交给子程序。

### 为什么项目里的 `build.sh` / `build.cmd` 不能直接作为命令启动？

命令开头的裸程序名只在 `PATH` 的绝对路径条目中查找，不会查找项目目录，防止打开不受信任的仓库时执行其中同名的程序。
请写成 `./build.sh`、`.\build.cmd` 或绝对路径；Linux / macOS 上脚本还需要执行权限。

### 支持图片显示、Kitty 键盘协议吗？

不支持。终端仿真是经过测试的 VT 子集：支持颜色与文字样式、备用屏幕、中文宽字符与常见 emoji、SGR 与传统鼠标协议；
不支持图像协议、像素级鼠标与 Kitty 键盘增强，滚动历史为纯文本。

## 安全与隐私

### CrewTerm 会把我的代码或数据发送到网络吗？

CrewTerm 本身不访问网络，也没有遥测。后台服务只通过本机的命名管道（Windows）或 Unix 域套接字（Linux / macOS）通信，
并且只接受当前用户的连接。你在终端中运行的 Agent CLI 会按它们自己的方式联网。

### 本地插件安全吗？

插件以你的用户身份运行，**没有沙箱**。插件安装后默认停用，启用前请检查它的清单与脚本，只安装你信任的插件。
详见 [本地插件](local-plugins.md)。

### 打开不受信任的仓库安全吗？

CrewTerm 做了几项防护：不从项目目录解析裸程序名；Windows 会话中的 cmd 不从当前目录查找程序；粘贴与提示词去掉控制字符，
无法注入按键；会话名、消息与插件输出中的终端控制序列不会被送到你的真实终端。但 Agent 本身在仓库中执行的操作由 Agent 负责。
安全边界见 [安全策略](../SECURITY.md)。

## 与其他工具的比较

以下比较基于 2026 年 10 月各项目的公开文档，其他工具的功能可能已经变化，请以它们的官方文档为准。

| | CrewTerm | tmux / zellij | herdr |
| --- | --- | --- | --- |
| 定位 | 多 Agent 终端工作台 | 通用终端多路复用器 | 面向 AI Agent 的终端多路复用器 |
| 分离后会话继续运行 | 是 | 是 | 是 |
| 感知 Agent 状态 | 是，基于 Provider 回调与显式上报 | 否 | 是，基于进程与屏幕输出检测 |
| 内置编辑器、文件区、Git、后台任务 | 是 | 否 | 否 |
| Agent 间消息与交接 | 定向收件箱、确认、交接、状态等待 | 否 | 通过 CLI / 套接字 API 互相发送提示、等待状态 |
| 多机器 | 否，只管理本机 | 各自在每台机器上运行 | 可在一个窗口中合并多台 SSH 机器 |
| 平台 | Windows、Linux、macOS | 主要面向 Linux、macOS 等类 Unix 系统 | Linux、macOS；Windows 为测试版 |

简单地说：如果你需要一个专注于终端复用、生态更大的工具，tmux、zellij 或 herdr 更合适；如果你希望在 Windows 上原生运行，
或者希望 Agent、代码、Git 与测试在同一个界面中，并且状态只基于明确的依据，CrewTerm 更合适。

## 参与项目

### 如何报告问题或提出建议？

在 [Issues](https://github.com/smallpeanutbaby/CrewTerm/issues) 中提交，附上系统、终端程序、`crewterm --version` 的输出与重现步骤。
安全问题请按 [安全策略](../SECURITY.md) 私下报告。

### 如何参与开发？

先阅读 [贡献指南](../CONTRIBUTING.md) 与 [开发者指南](development.md)。
