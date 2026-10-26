# CrewTerm 文档

CrewTerm 是在一个终端里同时运行、观察和协调多个 AI 编程 Agent 的跨平台工作台。本页是全部文档的入口，按读者的目的分组。
第一次使用请从 [快速入门](getting-started.md) 开始。

## 开始使用

| 文档 | 内容 |
| --- | --- |
| [快速入门](getting-started.md) | 安装，十分钟走完一次完整的多 Agent 流程：分屏、worktree、代码上下文、通知、离开与回来 |
| [使用手册](user-guide.md) | 工作台每个区域与功能的说明：项目、Agent、标签页与分屏、终端、编辑器、Git、任务、检查点 |
| [快捷键与命令参考](commands.md) | 全部快捷键、各区域内的按键、命令面板中的每一条命令、右键菜单 |
| [常见问题](faq.md) | 状态为何“未知”、断开后会怎样、数据在哪里、安全与隐私、与其他工具的比较 |
| [术语表](glossary.md) | 会话、原生会话、Provider、语义状态、交接等术语的含义 |

## 多 Agent 与自动化

| 文档 | 内容 |
| --- | --- |
| [Agent 集成指南](agent-integration.md) | 各内置 Agent 的接入方式、语义状态模型、Claude Code 与 Codex 的回调、协作语义、自定义 Agent |
| [多 Agent 工作流实战](workflows.md) | 实现者与审查者、脚本编排、测试失败交给 Agent、断开与恢复、插件通知等完整配方 |
| [CLI 使用说明](cli-guide.md) | 全部子命令、参数、输出字段与退出码，以及脚本示例 |
| [原生会话](native-sessions.md) | Provider 会话标识的记录、精确恢复、重新开始与遗忘，各 Provider 的适配范围 |

## 扩展与集成

| 文档 | 内容 |
| --- | --- |
| [本地插件](local-plugins.md) | 插件清单、命令与事件钩子、输入载荷、上限、完整示例与调试 |
| [插件示例](../examples/plugins/workspace-notes/README.md) | 可以直接安装运行的示例插件 |
| [后台服务协议](protocol.md) | 客户端与后台服务之间的传输、帧格式、请求与响应，供自行编写集成工具时参考 |
| [配置与数据参考](configuration.md) | 启动参数、环境变量、状态目录中的每个文件、设置字段、模板格式与全部数量上限 |

## 参与开发

| 文档 | 内容 |
| --- | --- |
| [开发者指南](development.md) | 环境、构建与调试、测试体系、常见改动的完整步骤、持续集成与发布 |
| [架构](architecture.md) | 分层与依赖规则、工程规范、跨平台设计、进程与线程模型、持久化、各轮审查结论 |
| [贡献指南](../CONTRIBUTING.md) | 报告问题、工程规范、测试与提交要求 |
| [行为准则](../CODE_OF_CONDUCT.md) | 社区行为准则 |

## 项目信息

| 文档 | 内容 |
| --- | --- |
| [更新日志](../CHANGELOG.md) | 各版本的变更 |
| [安全策略](../SECURITY.md) | 支持的版本、私下报告漏洞的方式、安全边界 |
| [许可证](../LICENSE) | MIT |
| [验证记录](verification/README.md) | 各版本开发过程中的功能验收矩阵与验证证据（历史记录） |

## 按任务查找

| 我想…… | 去看 |
| --- | --- |
| 同时运行两个 Agent 并让它们互不干扰 | [快速入门 §6](getting-started.md#6-为-agent-隔离出独立的-worktree)、[工作流实战](workflows.md) |
| 用脚本让一个 Agent 完成后把结果交给另一个 | [工作流实战](workflows.md)、[CLI 使用说明](cli-guide.md) |
| 知道 Agent 什么时候需要我 | [Agent 集成指南](agent-integration.md)、[使用手册](user-guide.md) |
| 重启电脑后继续原来的对话 | [原生会话](native-sessions.md) |
| 添加列表中没有的 Agent 或 SSH 命令 | [Agent 集成指南](agent-integration.md)、[配置与数据参考](configuration.md) |
| 在 Agent 完成时收到提醒或执行脚本 | [本地插件](local-plugins.md)、[工作流实战](workflows.md) |
| 备份、迁移或重置所有数据 | [配置与数据参考](configuration.md) |
| 修改 CrewTerm 本身 | [开发者指南](development.md)、[架构](architecture.md) |

文档以中文编写，与源码注释一致。英文概览见 [README.en.md](../README.en.md)。
