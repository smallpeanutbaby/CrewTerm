# CrewTerm 0.2 功能验收矩阵

本版范围是 **Windows 本地 C# 多 Agent 终端工作台**，包含本地插件；不包含插件市场、多平台客户端、多端同步或多机器管理。源码版本为 0.2.0。下表把实际入口、行为、证据和未验证范围对应起来，不能用一个模块的通过数代替整体验收。

最终全套测试、独立发布程序与发布记录见 [0.2 发布验证](verification-v02-release.md)。全套 Release 测试 464/464、发布程序验收 44/44 已通过，构建 0 警告、0 错误。

## 布局、终端与代码

| 功能 | 实际入口与行为 | 当前证据与边界 |
| --- | --- | --- |
| 多项目与 Agent | F2 添加本地项目；F3 启动已安装 CLI 或自定义命令；每个会话有独立工作目录、输入和屏幕 | 真实 ConPTY、CLI、TUI；provider 安装与登录由用户管理 |
| 命名标签页 | F9 → `Tab: New / Rename / Choose / Next / Previous`；也可点击标签 | [布局 RED/GREEN](verification-v02-layout.md)、源码中的 `LayoutTuiTests.cs` |
| 四向嵌套分屏 | F9 → `Terminal: Split right / left / down / up`；当前面板可继续分割；拖拽分隔线调整比例 | 分割树、比例边界、实际鼠标拖拽、三面板双轴输入和布局持久化已测；见 [布局证据](verification-v02-layout.md) |
| 选择会话与持久布局 | `Terminal: Choose session for active pane`、`Focus next pane`；每个项目保存标签、分屏比例和会话绑定 | 单元与发布版真实 TUI 通过；关闭面板/标签保留进程，选择已显示会话不重复绑定 |
| 鼠标与本地选区 | 普通鼠标在启用 tracking 时交给子程序；Shift 手势强制本地选择、右键菜单或历史滚动 | SGR、legacy、移动、拖动、释放和滚轮编码已测；[终端证据](verification-v02-terminal.md) |
| 复制、粘贴和右键 | 拖选默认复制；Ctrl+Shift+C 选区优先，无选区复制当前 screen；Ctrl+Shift+V 粘贴；右键提供选择/启动会话、分屏、复制与文件引用跳转 | 选区和回调由源码 `TerminalPaneSelectionTests.cs` 验证，窗口绑定位于 `WorkspaceWindow.Mouse.cs`；右键→分屏通过发布版真实 TUI |
| 历史与实时画面 | 滚轮浏览历史，F9 → `Terminal: View / search scrollback`；键盘输入回到实时画面 | 最多 2,000 行纯文本，历史不保留颜色或重排旧行；选区无越界自动滚屏；[终端证据](verification-v02-terminal.md) |
| 终端兼容 | ANSI/索引色/RGB、文字样式、备用屏幕、光标与查询、中文宽字符和常见组合 emoji | 真实输入字节、环境、resize 和 VT 单元测试；不是完整 xterm 或完整 Unicode 排版，详见 [终端边界](verification-v02-terminal.md) |
| 文件与代码 | 文件树、语法高亮、行号、编辑、撤销/重做、查找、行跳转、全文搜索；F5 保存 | 保留原有编码/换行与磁盘冲突保护；文件上限 4 MiB，搜索最多 500 条；[Workspace 证据](verification-workspace.md)，0.2 全套回归已通过 |
| 代码上下文 | F9 → `Editor: Send selected code to an agent`，附路径与行号 | 只向明确目标粘贴，不自动 Enter；目标需启用 bracketed paste；[App 证据](verification-app.md) |
| Git 与 worktree | F6 查看改动、diff、单文件暂存、worktree 创建/打开/删除 | 实际临时 Git 仓库测试；删除脏 worktree 由 Git 拒绝；[Workspace 证据](verification-workspace.md) |
| 文本检查点 | F9 → `Checkpoints: Capture / preview / restore` | 预览后变化会拒绝恢复，异步恢复不会覆盖新编辑的 buffer；最多 4,000 文件/64 MiB，逐文件原子保存不等于全项目原子恢复；[Workspace 证据](verification-workspace.md) |
| 后台任务与流水线 | F7；CLI `run / tasks / task / cancel`；UI 可保存命令和顺序流水线 | 实际退出码、输出、取消、失败即停止与历史；CLI 暂无独立 pipeline 子命令；[Automation 证据](verification-automation.md) |

## Agent 状态与协作

| 功能 | 实际入口与行为 | 当前证据与边界 |
| --- | --- | --- |
| 语义状态 | `agent-state`、`agent-report`；F9 → `Agent: Explain selected status`；Unknown/Working/Blocked/Done/Idle/Exited 带 Source、Reason、Revision | 持久化和真实 daemon/CLI 已测；普通输出或进程存活不推导完成；[Agent 证据](verification-v02-agent.md) |
| 优先级与未读 | 侧边栏优先等待输入，其次未读完成、未确认消息、工作中；排序变化保持所选会话身份 | 源码 `AgentTuiTests.cs` 和服务测试；发布版真实 TUI 通过 |
| 完成/等待通知 | `notifications`、`notification-read`；F9 → `Agent: Notifications / Mark selected as read` | Done/Blocked 转入时通知，重复观察不重复创建；列表读取不标为已读；确认 Blocked 不等于批准工具；[Agent 证据](verification-v02-agent.md) |
| 定向消息与收件箱 | `message-send / message-list / message-ack`；F9 → `Agent: Send message / Inbox`；CLI 可指定 `--reply-to` | 真实管道与 TUI 消息/确认；消息保存与向终端投递分开；只有收件方可确认，消息列表不自动输入；[Agent 证据](verification-v02-agent.md) |
| 提交提示与交接 | `agent-prompt`、`agent-handoff`；F9 → `Agent: Handoff prompt` 明确选择收件人 | 真实子进程接收输入与 Enter，返回 Delivered；目标退出/Blocked 时拒绝自动提交；成功投递不证明模型已执行；[Agent 证据](verification-v02-agent.md) |
| 状态等待 API / UI | `agent-wait ID --state done,blocked --timeout-ms 30000 [--after-revision N]`；F9 → `Agent: Wait for selected state` | API 有状态/revision/超时、取消及并发等待测试；源码 `AgentTuiTests.cs` 与发布版 UI 异步等待通过；最多 60 秒、8 个等待 |
| 协作范围 | 用户或本地调用者明确指定发送方、收件方、内容及等待条件 | 不声称模型自动拆分任务、自动协商、隐式广播或自动合并修改 |

## 原生恢复与 provider 回调

| 功能 | 实际入口与行为 | 当前证据与边界 |
| --- | --- | --- |
| 持久原生会话目录 | `sessions --all true`；保存 provider、原生 ID、命令与工作目录，保留休眠记录 | 多实例目录、重启、并发写入、新记录不覆盖旧记录已测；[Provider 证据](verification-v02-provider-plugins.md)、[daemon 证据](verification-v02-agent.md) |
| 精确恢复 | `session-bind`、`session-resume`；F9 → `Agent: Bind native conversation / resume command` 与 `Resume original conversation` | 真实本地 CLI fixture 经 daemon/ConPTY 收到原 UUID、profile/config 和含空格/`&` 的 cwd；从未以 `--last` 代替缺失 ID |
| 关闭/重新开始/遗忘 | `close`、`session-restart`、`session-forget` 是独立操作；F9 有对应入口 | 重复恢复不重复启动；忘记后不会被旧快照重新导入；provider 历史文件保留；[Agent 证据](verification-v02-agent.md) |
| 参数与未知 CLI | 保留已验证的 profile/model/settings/cwd 配置，去掉旧 selector 和初始 prompt；显式恢复命令优先 | 未知选项、可变参数数量、wrapper、复合命令等拒绝自动重建；见 [原生恢复说明](native-sessions.md) |
| Claude 自动身份和状态 | 普通新会话预分配 UUID；启动/恢复临时注入调用级 `--settings` command/args hook，stdout 静默，不输出审批决策 | 真实 fixture → 回调程序 → 管道已测；已有 `--settings`、`--bare`、`--safe-mode` 时保留原设置并跳过自动接入。Stop 仍有后台任务/cron 时不宣称 Done；[hook 证据](verification-v02-agent.md) |
| Codex 自动身份和状态 | 调用级 `notify` 在 turn-complete 到达时登记 `thread-id`，Working 可从提交输入推断 | 保留命令行及用户/profile 的既有 notify，跳过原因显示在 Activity.Reason；启动时不宣称已有 ID；不绕过 hook trust；内部切到另一已绑定 thread 时需明确重新 bind，防止旧回调误绑定；[hook 证据](verification-v02-agent.md) |
| 回调生命周期 | 只连接既有 daemon，有限等待；临时设置不写入原始恢复模板 | 已有不同原生 ID 时忽略非 SessionStart 的不匹配事件；不改全局认证/设置；[hook 证据](verification-v02-agent.md) |
| 实际 provider 验证 | 已安装 Claude Code 2.1.310、Codex CLI 0.163.0：help/version、真实无付费启动界面 | 没有发送模型提示、确认更新、改变登录或执行真实模型回合；**未实测模型服务对话历史回读**。Gemini/OpenCode/Copilot 本机未安装，仅验证官方 selector 与命令构造 |

原生恢复的“fixture 已通过”证明 CrewTerm 保存并发送了正确身份和参数，不证明 provider 服务已经保存/恢复了模型历史，也不证明其任意未来版本完全兼容。

## 本地插件与服务生命周期

| 功能 | 实际入口与行为 | 当前证据与边界 |
| --- | --- | --- |
| 本地插件目录 | `plugins / plugin-install / plugin-enable / plugin-remove`；F9 → `Plugins:*` | 安装默认停用，展示源目录和 manifest；移除注册不删除源文件；[插件证据](verification-v02-provider-plugins.md) |
| 命令与自动事件 | F9 显示启用命令；`plugin-run / plugin-event / plugin-history`；自动 created/exited/state/message 事件 | 实际 PowerShell 进程读取 JSON stdin；事件失败不阻止其他插件；[插件协议证据](verification-v02-agent.md) |
| 执行边界 | 字面参数、工作目录、超时、输出尾部、取消、进程清理、错误结果 | 默认 30 秒/65,536 字符，最高 300 秒/1,048,576 字符；不是安全沙箱；[插件说明](local-plugins.md) |
| 可运行示例 | [Workspace Notes](../examples/plugins/workspace-notes/README.md)：统计当前项目顶层文件、记录事件 JSON | 测试复制实际示例并启动真实进程；兼容 CLI workingDirectory 与 UI projectRoot；使用系统自带的 Windows PowerShell，无需另装运行时 |
| Detach / 重连 | F10 离开界面后 daemon 继续持有会话与后台任务；同一状态目录重连 | 真实 TUI 与输入队列排空测试；关闭界面不等于 stop；[App 证据](verification-app.md)、[布局证据](verification-v02-layout.md) |
| 旧 daemon 兼容 | 启动握手显示版本/协议差异，保护现有工作；新增功能需要新版服务 | 版本协议测试已测；**不会自动停止或升级旧服务**。用户须先完成/保存工作，明确 `stop` 并等待退出，再启动新版；可用独立 state-dir 并行试用 |
| 范围排除 | 插件市场、多平台客户端、多端同步、多机器 daemon 管理、远端文件系统 | 本轮未实现；本地自定义命令可运行用户已安装的程序，不因此提供远程管理功能 |

## 最终发布记录

| 验收 | 状态 |
| --- | --- |
| 全套 Release 测试 | `dotnet test CrewTerm.slnx -c Release --no-restore`：464/464，0 失败、0 跳过；Core 36、Terminal 62、Automation 136、Workspace 30、App 200 |
| 全套 Release 构建 | `dotnet build CrewTerm.slnx -c Release --no-restore`：0 警告、0 错误 |
| win-x64 self-contained 发布 | `scripts/publish.ps1` 已成功 |
| self-contained 发布程序 UI + CLI + hooks + version | 使用 `CREWTERM_TEST_EXE` 指向发布程序：44/44，0 失败、0 跳过；命令见 [发布验证](verification-v02-release.md) |
| 发布目录与版本 | win-x64 self-contained，0.2.0；具体发布记录见 [发布验证](verification-v02-release.md) |
| 付费模型对话及真实历史回读 | 未执行，不在本次自动验收结论内 |

分模块证据：[布局](verification-v02-layout.md)、[Agent/daemon/CLI/hook](verification-v02-agent.md)、[终端/鼠标](verification-v02-terminal.md)、[Provider/本地插件](verification-v02-provider-plugins.md)。最终统计以统一发布验证记录为准，不能简单相加各模块中不同时间或不同过滤器的数字。
