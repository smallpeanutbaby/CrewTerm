# CrewTerm

**用 C# 编写的原生终端多 Agent 工作台。** 在同一个界面里运行 Claude Code、Codex、其他 CLI 或 Shell，同时浏览项目、编辑代码、审查 Git 改动和运行测试。

CrewTerm 使用真实 Windows ConPTY 进程。每个会话拥有独立工作目录、输入和屏幕；F10 离开界面后，后台服务继续持有会话。重新打开会连接原来的服务。

## 启动

当前版本：0.1.0，Windows x64；源码需要 .NET 10 SDK。独立发布包自带运行时。

```powershell
cd D:\代码\CrewTerm
dotnet run --project src/CrewTerm.App -- --project "D:\代码\CrewTerm"
```

发布构建后，也可以运行根目录的 `CrewTerm.cmd`，或：

```powershell
.\artifacts\win-x64\crewterm.exe --project "D:\代码\CrewTerm"
```

建议用 Windows Terminal，窗口至少 120 列 × 35 行，选择支持中文的等宽字体。界面操作标签目前为英文；中文目录、源文件、终端输入和输出均可使用。

## 第一次使用

1. 按 **F2** 添加本地项目目录。
2. 按 **F3** 选择已安装的 Agent，或输入自定义命令。CrewTerm 会检查 PATH，不会替你安装、登录或绕过 Agent 自己的审批。
3. 再按 F3 启动第二个 Agent，按 **F8** 开启双终端；通过 F9 的 `Agent: Choose second split terminal` 选择右侧会话。
4. 按 **Alt+4** 进入文件列表，Enter 打开目录或文件；**Alt+3** 聚焦编辑器，**F5** 保存。
5. 按 **F10** 分离界面。重新启动 CrewTerm 可继续使用活着的终端和任务。

同一个目录内多个 Agent 的修改会相互可见。需要隔离时，用 F6 创建 Git worktree，再把 Agent 启动到各自的项目目录。

## 快捷键

| 按键 | 功能 |
| --- | --- |
| F1 | 帮助 |
| F2 / F3 | 添加项目 / 新建 Agent 或终端 |
| F4 / F5 | 显示或隐藏文件区 / 保存文件 |
| F6 / F7 | Git 操作 / 后台任务与流水线 |
| F8 / F9 | 双终端 / 可搜索命令面板 |
| F10 | 分离，保留后台会话和任务 |
| Alt+1 / Alt+2 | 聚焦左 / 右终端 |
| Alt+3 / Alt+4 / Alt+5 | 聚焦编辑器 / 文件列表 / Agent 列表 |
| Ctrl+Shift+P | 命令面板 |
| Ctrl+Shift+V / Ctrl+Shift+C | 粘贴到终端 / 复制终端当前屏幕 |
| Esc | 关闭弹窗；在终端内传给子程序；其他区域请求退出并检查未保存内容 |

命令面板和选择器可以直接输入文字过滤，按 ↓ 进入列表，Enter 选择。终端拥有普通按键、Tab、Ctrl+C、Esc；F1–F10 被工作台保留，需要发给子程序时使用 F9 的 `Terminal: Send reserved key`。

## 已实现

- **多项目与多 Agent**：项目持久化；内置 Claude Code、Codex、OpenCode、Gemini、Copilot、Shell 配置；可增加自定义 CLI/SSH 配置；独立终端、会话切换、双屏、重新连接、显式关闭。
- **终端**：VT 屏幕、ANSI 颜色、光标、备用屏幕、中文宽字符、滚动历史、终端查询回复、窗口缩放；支持启用 SGR 鼠标模式的子程序接收点击、释放和上下滚轮。
- **代码**：目录浏览、语法高亮、行号、编辑、撤销/重做、文本查找、跳转行、项目全文搜索；保存保持原编码与换行格式。磁盘被 Agent 改动后拒绝覆盖，先检查或重新加载。
- **代码上下文**：把选中行或当前行插入指定 Agent 的输入区，并附带文件路径及行号；只有目标启用 bracketed paste 时允许发送，不自动按 Enter。
- **Git**：工作区/暂存区改动、文件 diff、单文件暂存、worktree 列表/创建/打开/删除；Git 拒绝删除脏 worktree。
- **任务**：后台运行任意构建/测试命令，实时输出、实际退出码、取消、历史；命令模板；顺序流水线、失败即停止；并发数默认 4，可配置为 1–32。
- **文件检查点**：捕获项目文本文件、预览恢复差异、确认恢复；预览后文件又变化时拒绝恢复；恢复期间新写的编辑内容保持在编辑器中。
- **命令行接口**：启动/列出/读写/关闭终端，启动/查看/取消后台任务。适合脚本调用，详见 [CLI 使用说明](docs/cli-guide.md)。

## 会话与数据

默认状态目录为 `%LOCALAPPDATA%\CrewTerm`，可以用 `--state-dir PATH` 创建互相独立的工作台。目录包含项目、布局、Agent 配置、命令/流水线模板、会话启动信息、任务历史和文件检查点。后台通信只允许当前 Windows 用户访问。

- **F10 / 关闭界面**：保持后台进程运行。
- **Agent: Terminate selected session / `close ID`**：终止指定终端及其子进程。
- **`cancel ID`**：取消一个后台任务。
- **Workspace: Stop all / `stop`**：终止整个后台服务的终端和任务。
- **电脑或服务重启**：可以恢复配置和已完成任务历史；不能恢复已消失的进程内存。通过 F9 的 `Relaunch a saved session` 显式重新运行保存的命令。

Agent 使用自己的认证和计费机制。CrewTerm 不保存额外的模型 API Key，不伪造 Token、成本或“已完成”状态。进程仍存活不表示 Agent 已完成工作。

## 当前边界

- 此版本针对 **Windows x64** 验证；未实现 Linux/macOS PTY 后端。
- 是多 Agent 终端与工作流管理器；不会自动让模型替你拆解任务、互发消息或合并代码。流水线执行用户配置的真实命令。
- SSH 使用实际 `ssh` 命令；文件区浏览本地项目，未实现远端文件系统或跨机器 daemon 管理。
- 终端为常用 VT 子集，不是完整 xterm；暂不支持图像协议、旧式鼠标编码、鼠标移动报告和横向滚轮。历史内容从 F9 的 Scrollback 查看。
- 文件编辑上限 4 MiB，搜索最多返回 500 条；检查点最多 4000 个文件/64 MiB，总体恢复有逐文件检查但不是跨文件原子事务。恢复时应停止修改这些文件的 Agent。
- 多界面同时修改配置时会报告冲突；不会静默覆盖其他界面刚保存的配置。多个界面同时调整同一会话尺寸时，以实际最后一次 resize 为准。
- 终端会话最多 32 个；后台日志有长度上限，历史最多保存最近 100 条。状态目录中可能包含命令输出和源代码检查点，请按项目需要管理它。

## 开发与验证

```powershell
dotnet test CrewTerm.slnx -c Release
dotnet build CrewTerm.slnx -c Release --no-restore
dotnet publish src/CrewTerm.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

测试包括真实文件/Git 仓库、ConPTY 子进程、命名管道 daemon、CLI 进程和整个 TUI 的键盘操作。RED/GREEN 过程与最终验证见 `docs/verification-*.md`。

本次交付验证：**212 项 Release 测试通过，0 失败、0 跳过；构建 0 警告、0 错误；独立发布程序的 7 项 TUI 验收全部通过。** 已用真实 ConPTY 启动本机 Claude Code 2.1.310、Codex CLI 0.163.0 的版本命令，均退出 0。

结构：`Core` 管理状态与导航；`Terminal` 实现 ConPTY/VT；`Workspace` 负责文件、Git、检查点；`Automation` 负责任务/Agent 配置/流水线；`App` 实现 daemon、CLI 与 Terminal.Gui 界面。
