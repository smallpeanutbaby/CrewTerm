# CrewTerm v0.2 Agent、daemon 与 CLI 验证

环境：Windows x64，.NET 10.0.302。仓库为 `D:\代码\CrewTerm`。App 测试使用独立 `AgentTests` 构建配置和每例独立临时状态目录，避免锁住用户正在运行的 Debug daemon。未停止用户默认实例，未发送付费 AI 提示。

按已确认的 v0.2 设计执行 TDD。两项执行技能在本环境不可用，按计划手工逐项执行 RED → GREEN → 复查。本文是验收记录，不为说明性文字添加人工测试。

## 已观察的 RED / GREEN

| 范围 | RED | GREEN |
|---|---|---|
| 语义状态与邮箱 | `dotnet test tests/CrewTerm.Automation.Tests --filter AgentActivityStoreTests --no-restore --verbosity minimal`：缺少新 API | 初始 9/9，通过真实文件验证状态、通知去重、显式确认、持久定向消息、有界记录、异步等待和取消 |
| 状态管道协议 | `dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter DaemonAgentProtocolTests --no-restore --verbosity minimal`：8 个案例返回未知操作 | 8/8，真实 ConPTY 提示、定向交接、等待、16 个并发等待时仍可报告状态、通知/邮箱跨 daemon 重启 |
| 可执行 CLI | `dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter AgentCliIntegrationTests --no-restore --verbosity minimal`：首批 5 项未知命令 | 首批 5/5；继续添加 native/plugin 两项并观察未知命令，接入后 7/7 |
| 原生续接集成 | `dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter DaemonNativeSessionTests --no-restore --verbosity minimal`：5 项未知操作/子环境缺失 | 初始 5/5：实际临时 `.cmd` fixture 在 daemon 重启后收到相同 native UUID，新增会话不覆盖 dormant 记录，重复 resume 不重复启动 |
| 忘记记录回归 | `--filter ForgettingExitedSessionIsNotUndoneByLegacyMetadataOnRestart`：已忘记记录被旧快照重新导入 | 更新兼容快照后通过；`AgentActivityStore.Forget` 先缺少 API，再通过活动/通知/邮箱清理测试，活动模块共 10/10 |
| 本地插件协议 | `dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter DaemonPluginProtocolTests --no-restore --verbosity minimal`：先缺少响应字段，再观察 7 项未知操作 | 7/7：实际 PowerShell 7 进程读取 JSON stdin、输出、启用/停用、删除仅移除注册、超时、失败事件及四类自动事件 |
| 输入与状态优先级 | `--filter DaemonAgentProtocolTests`：直接终端提交后仍是旧 hook Done，而非 Working | 11/11；已知 provider 的回车提交标为 `input` 推断，审批屏幕仍能转 Blocked，普通 shell 不推断 Agent 正在工作 |
| 元数据与启动竞争 | `--filter DaemonNativeSessionTests`：保存失败遗留 legacy 幽灵记录、快照缺少 provider；竞争 host 未拿到锁就把 Working 改成 Exited | 最终 12/12；原生 catalog 权威，先持久化原生资料再保存兼容快照，失败回滚，持有 daemon 锁后才迁移/标记 dormant |
| 原始命令保留 | `--filter LegacySnapshotRetainsOriginalCommandWithoutTransientGeneratedHookSettings`：legacy Command 含临时 `--session-id`/`--settings` | 通过；兼容快照与 native catalog 保留用户原始启动命令，实际进程使用单次启动配置 |
| 版本与旧服务 | `--filter VersionCompatibilityTests`：5 项失败，仍显示 0.1.0，旧 daemon 没有提示 | 5/5；帮助、版本、doctor、ping 使用项目单源 0.2.0，ping 附 protocol 2；真实 exe 对模拟旧管道保持 list/read/stop，并拒绝新操作且说明显式重启 |
| Hook 解析与配置 | `dotnet test tests/CrewTerm.Automation.Tests --filter AgentHookTests --no-restore --verbosity minimal`：先缺少 API；新增 `--config=notify` / 用户通知 / profile 通知保护分别观察行为失败 | 最终 25/25；已知事件及 native UUID、未知事件忽略、后台任务未结束不称 Done、独立设置文件、保留已有通知 |
| Hook 可执行链路 | `dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter ProviderHookIntegrationTests --no-restore --verbosity minimal`：6 项失败（未知 CLI/API、无生成配置、回调错误启动 daemon） | 6/6；真实独立 daemon + ConPTY fixture + `crewterm.exe agent-hook` + 管道，验证 stdin / argv JSON、静默 stdout、native ID、旧 ID/关闭会话忽略、缺失服务不重启 |

Hook 测试修正记录：官方当前配置参考明确项目 `.codex/config.toml` 中的 `notify` 被 Codex 忽略，通知属于用户 / profile 层。最初将项目 notify 也视为生效配置的测试不符合该规则，按文档改为“项目 notify 不阻止调用级通知”，同时新增 profile 保护；观察 2 项预期 RED 后实现并通过。另将 hook 集成测试改为使用子进程专用 `CODEX_HOME`，避免用户已有通知配置使 fixture 按设计跳过自动接入；父进程环境与用户配置均未修改。[配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)。

## 行为约定

- `SessionInfo` 保持原有字段，追加 `Activity`、`UnreadCount`、`Priority` 和原生会话资料。`list` 仍只列 daemon 当前持有的会话；`session-catalog` 包含持久的 dormant 记录。
- `Activity.State` 是 Unknown、Working、Blocked、Done、Idle 或 Exited；同时提供 Source、Reason、ChangedAt、Revision。进程存活与普通输出不代表任务成功。
- `agent-prompt` 仅向明确的存活、未 Blocked 目标发送文本及回车。其 `prompt` 来源表示输入已投递，不能证明模型已开始执行；后续识别到审批文本仍可成为 `screen` 来源的 Blocked。显式 `api`/`hook` 状态不会被旧屏幕文本覆盖。
- 普通终端 `input` 对已知 provider 的 CR / CRLF 提交会以 `input` 来源进入 Working；只是提交证据，Reason 明确说明推断，不能替代原生事件。新提交会释放上一轮 hook Done 对屏幕推断的覆盖；投递失败则恢复原状态（仅当没有更新的事件）。
- Done/Blocked 进入时产生通知；重复观察不会重复通知。列表读取不修改已读状态。完成通知被明确确认后，仍处于 Done 的活动转为 Idle；Blocked 确认后继续保持等待状态。
- 优先级为 Blocked、未读 Done、未确认消息、Working。通知最多 200 条，消息最多 500 条。消息有明确发送方、收件方和确认；消息列表不自动向终端输入。`agent-handoff` 才会尝试向明确目标投递，并返回 Delivered。
- `agent-wait` 采用异步 revision 信号，最大 60 秒，最多 8 个等待；超时返回错误。插件长请求最多 4 个，16 个连接中保留控制容量。
- 本地插件命令及事件均经过已安装清单；默认安装为停用。事件包括 `session.created`、`session.exited`、`agent.state`、`message.received`。stdin envelope 的 payload 为 `{stateDirectory, sessionId, detail}`。自动事件队列上限 32，最近结果上限 100，可通过 `plugin-history` 查询；这是本地进程执行，不是安全沙箱。
- CLI 成功打印 JSON；校验、等待超时和插件失败返回退出码 1。恢复原始对话与重新启动是两个不同命令，缺少身份时不会用 `--last` 代替。
- `agent-hook` 是例外：成功 stdout/stderr 为空、退出 0，不输出权限决策，不创建 daemon；单次处理最多 2.5 秒，输入上限 65536 字符。Daemon 协议本身仍返回可观察 JSON，便于集成测试和诊断。
- 旧 daemon 的 ping 可以继续读取。`CrewTermCommandLine.ServerCompatibilityWarning` 供 UI 显示；旧命令与旧任务操作保留，新 Agent/native/plugin 操作在 CLI 发送前给出明确重启要求。不自动终止用户会话。

## Provider 文档依据与边界

Claude 命令 hook 支持独立 executable/args，输入来自 stdin；调用级 `--settings` 可以引用生成的设置文件。状态回调只观察，不输出权限决策。[Claude hooks](https://code.claude.com/docs/en/hooks)、[Claude CLI](https://code.claude.com/docs/en/cli-reference)。

自动注入仅适用于可解析的 Claude / Codex 原生调用，不修改全局配置。Claude 的已有 `--settings`、`--bare`、`--safe-mode` 保留且跳过自动接入；Codex 的显式 notify 与用户/任一 profile 文件中的已有 notify 均保留。为避免覆盖从配置选中的 profile，profile 检测采用保守策略，未启用 profile 中的 notify 也会使自动接入跳过。初始 Activity.Reason 解释连接状态；用户可继续用 `agent-report` / `session-bind` 显式接入。

Claude 的 Stop 遇到尚有 background_tasks / session_crons 时保持 Working；SessionEnd 表示原生对话结束或切换，真正 Exited 来自进程。SessionStart 允许切换已绑定的 Claude 身份；其他已绑定 ID 不匹配的回调被忽略，防止旧对话事件改写当前状态。Codex 在终端内另开 thread 后需要显式 `session-bind` 新 ID。

Codex `notify` 当前在 turn 完成时调用外部程序，并把 JSON 作为一个参数追加，其中包含原始 thread ID。它不提供所有阶段的状态事件，因此不能声称启动时已通过该回调捕获身份。[Codex notifications](https://learn.chatgpt.com/docs/config-file/config-advanced#notifications)。

原生恢复过程使用可控本地 fixture 验证，真实 provider 仅执行帮助/版本等无付费查询。真实模型回合结束后的回调需要单独在用户实际会话中观察，不能从 fixture 测试推断已完成真实模型验收。

2026-10-26 再次执行本机 `claude.cmd --version/--help` 与 `codex.cmd --version/--help`：分别为 Claude Code 2.1.310、codex-cli 0.163.0；help 确认 Claude `--settings`、`--session-id`、`--resume`，Codex `-c/--config`、profile 文件及 resume。没有调用模型，也未添加 bypass-hook-trust。

## 最终模块验证

2026-10-26 实际结果：

```powershell
dotnet test tests/CrewTerm.Automation.Tests --no-restore --verbosity minimal
# 136 passed, 0 failed, 0 skipped

dotnet test tests/CrewTerm.App.Tests -c AgentTests --filter "FullyQualifiedName~Daemon|FullyQualifiedName~CliIntegrationTests|FullyQualifiedName~ProviderHookIntegrationTests|FullyQualifiedName~VersionCompatibilityTests" --no-restore --verbosity minimal
# 76 passed, 0 failed, 0 skipped

dotnet build src/CrewTerm.App -c AgentTests --no-restore --verbosity minimal
# build succeeded, 0 warnings, 0 errors
```

本次模块没有运行全 App TUI 套件，以避免与主任务的真实 UI 验收争抢资源。全解决方案 Release、UI 及自包含包验收由主任务统一执行。CLI / provider hook / version 测试支持 `CREWTERM_TEST_EXE` 指向发布程序；本页以上结果使用当前 AgentTests 构建，不冒称为发布包验收。
