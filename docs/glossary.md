# 术语表

本文解释 CrewTerm 文档与界面中使用的术语，按主题分组。括号中是界面、CLI 或源码中对应的英文名称。

## 运行结构

| 术语 | 含义 |
| --- | --- |
| 工作台（workspace） | `crewterm` 打开的终端用户界面，包含侧栏（`spaces`、`agents` 与 `files`）、终端面板、代码查看器与状态栏。它只是客户端，可以随时离开与重新连接 |
| 后台服务（daemon） | 每个状态目录一个的后台进程（`crewterm --server`），持有全部终端会话、后台任务与插件执行。界面与 CLI 都通过它工作 |
| 状态目录（state directory） | 保存项目列表、布局、设置、Agent 配置、模板、会话目录、状态、收件箱、插件登记、任务历史与检查点的目录；`--state-dir` 指定 |
| 分离（detach） | 按 F10 离开界面，后台服务及其会话、任务继续运行 |
| 停止（stop） | `crewterm stop` 或 `Workspace: Stop all ...`：结束后台服务及其全部会话与任务 |
| 协议版本（protocol version） | 客户端与后台服务之间的接口版本。连接到旧版服务时界面显示 “OLD SERVER”，只允许基础操作 |
| 伪终端（PTY） | 让程序以为自己运行在真实终端中的机制：Windows 上是 ConPTY，Linux / macOS 上是 `forkpty` |
| 进程树 | 会话启动的程序及其全部子进程。结束会话时整体结束：Windows 用作业对象（job object），Unix 用进程组 |

## 项目与布局

| 术语 | 含义 |
| --- | --- |
| 项目（project） | 登记在工作台中的本地目录（F2 添加）。会话、侧栏 `files`、Git 与后台任务都以项目根目录为范围 |
| 标签页（tab） | 一组终端面板及其分屏布局，可以命名、切换与关闭 |
| 面板（pane） | 显示一个会话的终端区域。关闭面板只改变布局，会话继续运行 |
| 分屏（split） | 把面板向右、左、下、上分割，可以继续嵌套；分隔线可拖动调整比例 |
| 布局（layout） | 每个项目的标签页、分屏树、比例与面板中显示的会话，按项目保存 |
| worktree | Git 的附加工作目录：同一仓库的另一个分支检出到独立目录。用来隔离多个 Agent 的修改 |

## 会话与 Agent

| 术语 | 含义 |
| --- | --- |
| 会话（session） | 后台服务中的一个终端进程，有自己的进程树、工作目录、输入与屏幕。会话 ID 是 CrewTerm 分配的标识 |
| Agent | 在会话中运行的 AI 编程命令行程序，例如 Claude Code、Codex；普通 Shell 也作为一种会话出现在侧栏 `agents` 中 |
| Provider | Agent CLI 的种类（`claude`、`codex`、`opencode`、`gemini`、`copilot` 等），决定状态接入与原生会话恢复的方式 |
| Agent 配置（profile） | 可以从 F3 启动的命令：内置的 Agent，或你保存的自定义 CLI / SSH 命令 |
| 原生会话（native session） | Provider 自己的对话标识，例如 Claude Code 的会话 UUID、Codex 的 thread ID、OpenCode 的 `ses_...`。与 CrewTerm 会话 ID 分开保存 |
| 会话目录（session catalog） | 持久保存的会话记录：Provider、原生会话标识、命令与工作目录。`crewterm sessions --all true` 查看 |
| 休眠记录（dormant） | 会话目录中已经没有实时进程的记录，可以恢复、重新开始或遗忘 |
| 恢复（resume） | 用已记录的精确原生会话标识重新启动 Agent，续接原来的对话 |
| 重新开始（restart） | 用保存的命令开始一个新对话，不声称继承原对话 |
| 遗忘（forget） | 删除已关闭会话的 CrewTerm 记录；不删除 Provider 自己的对话文件 |
| 恢复命令（resume command） | 为无法自动重建的命令显式登记的完整恢复命令（`session-bind --resume-command`） |
| 回调（hook） | Agent CLI 在生命周期事件中调用的程序。CrewTerm 只为自己启动的这一次附加回调（Claude Code 的调用级 `--settings`、Codex 的 `notify`），用于获得原生会话标识与状态 |

## 状态与协作

| 术语 | 含义 |
| --- | --- |
| 语义状态（activity state） | Agent 当前在做什么：`Unknown`（未知）、`Working`（工作中）、`Blocked`（等待输入或决策）、`Done`（完成且未确认）、`Idle`（空闲）、`Exited`（已退出） |
| 来源（Source） | 状态的依据，例如 Provider 回调或通过 `agent-report` 的显式上报（`api`） |
| 原因（Reason） | 状态变化的说明文字，`Agent: Explain selected status` 中显示 |
| 版本号（Revision） | 每次状态变化递增的数字；`agent-wait --after-revision` 用它只等待更新的变化 |
| 通知（notification） | 进入 `Done` 或 `Blocked` 时产生的提醒；读取不会标为已读，需要明确确认 |
| 优先级（priority） | 侧栏 `agents` 的排序：等待输入 → 完成未读 → 有未确认消息 → 工作中 |
| 空间（space） | 侧栏 `spaces` 中的一个项目；状态点汇总其中最需要关注的 Agent 状态，右侧显示 Git 分支 |
| 文件列表（files） | 侧栏最下面的分区，浏览当前项目的一个目录；每 2 秒自动重新读取，单击文件在代码查看器中打开；见 [使用手册 8.1](user-guide.md#81-侧栏-files) |
| 代码查看器（code viewer） | 打开文件时出现在终端区下方的编辑器：行号、语法高亮、可编辑，F5 保存，Esc 回到终端，F4 显示 / 隐藏 |
| 前缀键（prefix key） | 先按 `Ctrl+b` 再按一个键执行工作台操作，仿照 herdr / tmux；见 [命令参考 1.1](commands.md#11-前缀键-ctrlb) |
| 收件箱（inbox） | 发给某个会话的定向消息列表。消息入箱与写入终端是两个独立的动作 |
| 确认（acknowledge） | 收件方明确标记一条消息已处理（`message-ack`）；只有收件方可以确认 |
| 提示投递（prompt） | 把文字写入目标 Agent 的终端并回车（`agent-prompt`）。目标正在等待审批或已退出时拒绝投递 |
| 交接（handoff） | 先把消息存入目标的收件箱，再尝试把同样内容作为提示投递（`agent-handoff`） |
| 状态等待（wait） | 等待某个会话进入指定状态之一，带超时（`agent-wait`） |
| 代码上下文（code context） | 编辑器中选中的代码，连同路径与行号，以 bracketed paste 插入目标 Agent 的输入区，由你确认后再提交 |

## 终端

| 术语 | 含义 |
| --- | --- |
| bracketed paste | 终端程序声明的一种粘贴模式：粘贴内容被特殊序列包围，程序据此区分“粘贴”与“键入”。CrewTerm 只在目标启用它时投递多行内容 |
| 保留键（reserved key） | 由工作台使用的按键（F1–F10 等）；用 `Terminal: Send reserved key` 发给子程序 |
| 鼠标跟踪（mouse tracking） | 子程序请求接收鼠标事件的模式。此时按住 Shift 可以强制在本地选择、打开菜单或滚动历史 |
| 滚动历史（scrollback） | 移出屏幕顶部的输出，最多保留 2,000 行纯文本 |
| 备用屏幕（alternate screen） | 全屏程序（编辑器、Agent 的 TUI）使用的独立屏幕缓冲区，退出后恢复原来的内容 |

## 工具

| 术语 | 含义 |
| --- | --- |
| 后台任务（task） | 在后台服务中运行的非交互命令（`crewterm run` 或 F7），标准输入关闭，记录输出与真实退出码 |
| 流水线（pipeline） | 按顺序运行的多个命令步骤，任一步失败即停止 |
| 命令模板 / 流水线模板 | 保存在状态目录中的常用命令与流水线，可以从 F7 运行 |
| 检查点（checkpoint） | 捕获的项目文件快照；可以预览差异并在确认后恢复 |
| 本地插件（local plugin） | 从本地目录登记的程序，由 JSON 清单（manifest）声明命令与事件钩子，安装后默认停用 |
| 事件（event） | 后台服务在会话创建、退出、状态变化、收到消息时自动发出的通知（`session.created` 等），也可以用 `crewterm plugin-event` 手动发出任意事件名 |
| 事件钩子（hook） | 插件清单中声明的“某个事件发生时运行哪条命令”，与 Provider 的生命周期回调是两回事 |

相关文档：[使用手册](user-guide.md)、[Agent 集成指南](agent-integration.md)、[配置与数据参考](configuration.md)、[架构](architecture.md)。
