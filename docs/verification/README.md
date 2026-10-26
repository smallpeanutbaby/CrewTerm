# 验证记录

本目录保存 CrewTerm 各版本开发过程中的功能验收矩阵与验证证据：当时运行了哪些测试、在什么环境中、覆盖了什么、哪些没有覆盖。
它们是历史记录，按当时的状态保留，不随新版本改写；记录中的测试数量、路径与平台以记录日期为准。
当前版本的功能与限制以 [README](../../README.md) 和 [文档首页](../README.md) 为准，最新的测试结果以持续集成为准。

## 0.2

| 记录 | 内容 |
| --- | --- |
| [0.2 功能验收矩阵](v02-features.md) | 0.2 每项功能的入口、行为、证据与未验证范围 |
| [0.2 最终验收](verification-v02-release.md) | 全套 Release 测试、发布程序验收与发布记录 |
| [Agent、daemon 与 CLI](verification-v02-agent.md) | 语义状态、通知、收件箱、交接、等待、原生会话、Provider 回调与插件协议 |
| [布局](verification-v02-layout.md) | 标签页、四向嵌套分屏、比例与布局持久化 |
| [终端与鼠标](verification-v02-terminal.md) | 鼠标协议、本地选择、复制粘贴、滚动历史与终端兼容 |
| [Provider 与本地插件](verification-v02-provider-plugins.md) | Provider 命令构造与恢复参数、本地插件目录与执行边界 |

## 0.1

| 记录 | 内容 |
| --- | --- |
| [Core](verification-core.md) | 基础设施层的测试驱动开发记录 |
| [Terminal](verification-terminal.md) | ConPTY 会话与 VT 屏幕 |
| [Workspace](verification-workspace.md) | 文件、编辑、Git 与检查点 |
| [Automation](verification-automation.md) | 后台任务、daemon、CLI 与窗口缩放 |
| [App](verification-app.md) | 工作台界面与回归证据 |

0.3 起，跨平台（Windows、Ubuntu、macOS）的验证由每次推送的[持续集成](https://github.com/smallpeanutbaby/CrewTerm/actions/workflows/ci.yml)完成，
不再单独撰写验证记录；各轮审查结论记录在 [架构文档 §7](../architecture.md#7-审查结论)。
