# 原生会话恢复

CrewTerm 会话 ID 与模型 CLI 的原生 conversation ID 分开保存。`native-sessions.json` 记录名称、初始命令、工作目录、provider、原生 ID、显式恢复命令和更新时间。新建会话不会覆盖尚未恢复的旧记录。

- 关闭结束实时进程并保留登记。
- 恢复使用已登记的精确原生 ID；已经运行的会话不会再启动一份。
- 重新开始明确创建新对话，不宣称继承原对话。
- 遗忘移除 CrewTerm 登记，不删除 provider 对话文件。运行中的记录需先关闭。

```powershell
crewterm session-bind CREWTERM_ID --provider codex --native-id NATIVE_UUID
crewterm session-resume CREWTERM_ID
crewterm session-restart CREWTERM_ID
crewterm session-forget CREWTERM_ID
```

未知 CLI、包装脚本或不能可靠拆分的命令，可明确登记完整恢复命令：

```powershell
crewterm session-bind CREWTERM_ID --provider custom --resume-command "my-agent resume exact-conversation-id"
```

## 适配范围

| Provider | 精确恢复入口 | 验证范围 |
| --- | --- | --- |
| Claude | `claude --resume UUID` | 本机 2.1.310 help、官方文档、真实本地参数 fixture |
| Codex | `codex resume UUID` | 本机 codex-cli 0.163.0 help、官方文档、真实本地参数 fixture |
| Gemini | `gemini --resume UUID` | 官方 CLI 文档、命令构造测试；本机未安装 |
| OpenCode | `opencode --session ses_ID` | 官方 CLI 文档、命令构造测试；本机未安装 |
| Copilot | `copilot --resume=UUID` | 官方 CLI 文档、命令构造测试；本机未安装 |

Claude 的普通新启动预分配 `--session-id UUID`。已存在的精确选择会被登记；fork 和禁止会话持久化的启动不会误用旧 ID。Codex 普通启动没有可预分配的会话 ID：其官方 `notify` 回调在 turn 完成时给出 `thread-id`，只有捕获/绑定该 ID 后才能精确恢复。未捕获时会显示原因；不会偷偷选择 `--last`。空对话、CLI 登录失败或禁用持久化不等于已经保存了可恢复的 provider 历史。

Claude/Codex 的恢复会保留已经验证参数数量的配置选项，例如 Claude `--settings`、`--model`、`--permission-mode`、`--system-prompt`，以及 Codex `--profile`、`--config/-c`、`--cd`、`--model`。值的原始引号保留；旧会话选择器和初始用户 prompt 会去掉，避免重发任务。重新开始也保留已识别配置选项。

自动重建有明确边界：未知选项、可变数量参数（例如 `--image`、Claude `--add-dir`）、shell 复合命令、wrapper 可执行文件、缺少参数值、不平衡引号或不能明确拆分的位置参数，必须登记完整恢复命令。Gemini/OpenCode/Copilot 当前仅自动处理已验证的会话选择器，其余配置参数同样需要显式恢复命令。显式命令优先执行；CrewTerm 不猜测未知 CLI 的参数语义。

## Hook 依据和验证限制

Claude 调用级 `--settings` 支持 command/args hook，stdin 含 `session_id`、`hook_event_name` 等。Codex `notify` 接收最后一个 argv JSON，含 `thread-id`；当前事件是 `agent-turn-complete`。Codex 的非 managed 生命周期 hook 需要用户在 `/hooks` 信任具体定义，CrewTerm 不应自动绕过 hook trust。回调采集只登记身份与状态，不输出权限允许/拒绝决策，不修改全局认证或配置。

测试使用可控本地进程收集真实 argv，并验证持久化重读后传递同一个 UUID；没有发送付费模型 prompt。因此自动测试不等于已实测模型服务保存、加载、计费或跨版本迁移历史。

依据：[Claude CLI](https://code.claude.com/docs/en/cli-reference)、[Claude hooks](https://code.claude.com/docs/en/hooks)、[Codex CLI](https://learn.chatgpt.com/docs/developer-commands?surface=cli)、[Codex notify](https://learn.chatgpt.com/docs/config-file/config-advanced)、[Codex hooks](https://learn.chatgpt.com/docs/hooks)、[Gemini sessions](https://geminicli.com/docs/cli/session-management/)、[OpenCode CLI](https://opencode.ai/docs/cli/)、[Copilot CLI](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)。
