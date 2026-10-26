# CrewTerm 0.2.0 最终验收

日期：2026-10-26。环境：Windows x64、.NET SDK 10.0.302、.NET Runtime 10.0.10。源码目录：`D:\代码\CrewTerm`。

## 确认的范围

本地 Windows 版本实现多标签、任意方向嵌套终端面板与分隔线拖拽、Agent 语义状态与通知/优先级/已读、定向消息与交接/等待、准确原生会话 ID 续接、本地插件命令与事件，以及鼠标选择/滚动/右键和常用 VT 模式。现有代码编辑、Git、任务、流水线和检查点功能保留。

用户明确排除插件市场、多端/跨平台、多机器统一管理。没有下载市场、远端 daemon 或跨机器文件管理。

详细入口和测试对应关系见 [功能验收表](v02-features.md)。各模块的 RED、GREEN、回归原因见 `verification-v02-layout.md`、`verification-v02-agent.md`、`verification-v02-provider-plugins.md`、`verification-v02-terminal.md`。

## 最终源码测试与构建

以下命令实际运行并退出 0：

```powershell
dotnet test CrewTerm.slnx -c Release --no-restore --logger trx --results-directory work/verification-v02-release
dotnet build CrewTerm.slnx -c Release --no-restore
```

| 测试程序集 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| Core | 36 | 0 | 0 |
| Terminal | 62 | 0 | 0 |
| Automation | 136 | 0 | 0 |
| Workspace | 30 | 0 | 0 |
| App | 200 | 0 | 0 |
| 总计 | **464** | **0** | **0** |

Release 构建为 **0 警告、0 错误**。原始 TRX 在源码 `work/verification-v02-release` 中；该目录为工作记录，不打进运行包。

## 自包含发布程序验收

```powershell
& .\scripts\publish.ps1
$env:CREWTERM_TEST_EXE = (Resolve-Path artifacts/win-x64/crewterm.exe).Path
dotnet test tests/CrewTerm.App.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~Tui|FullyQualifiedName~CliIntegrationTests|FullyQualifiedName~ProviderHookIntegrationTests|FullyQualifiedName~VersionCompatibilityTests' --logger trx --results-directory work/verification-v02-published
.\artifacts\win-x64\crewterm.exe --version
.\artifacts\win-x64\crewterm.exe --help
```

发布成功；以上发布版筛选 **44/44 通过，0 失败、0 跳过**。版本输出 `CrewTerm 0.2.0`，帮助包含新 CLI、标签/面板、鼠标和选区复制入口。运行包包含 `coreclr.dll` 与 `includedFrameworks: Microsoft.NETCore.App 10.0.10`，不要求用户先安装 .NET。

发布版测试覆盖真实 ConPTY UI、四向嵌套布局及鼠标拖动、分离重开、输入隔离、Agent 通知/协作/等待、准确会话恢复参数、本地插件执行和回调、旧服务版本提示。测试专门通过 `CREWTERM_TEST_EXE` 选择发布的可执行文件，不是重复测试源码 exe。原有 CLI 测试仅新增该测试路径覆盖开关，原断言保持不变。

## RED → GREEN 示例与额外修复

```powershell
dotnet test tests/CrewTerm.App.Tests -c Release --filter 'FullyQualifiedName~CooperationMenus|FullyQualifiedName~LegacyServerTuiTests' --no-restore
```

实际 RED：2 失败、0 通过；缺少等待命令和旧服务提示。实际 GREEN：2 通过。旧服务测试继续增加未知操作保护断言，观察 `Unknown operation: notifications` 的 RED；修复后 1/1 通过，并断言没有发出 `notifications` 或 `stop`。

```powershell
dotnet test tests/CrewTerm.App.Tests -c Release --filter FullyQualifiedName~SecondPaneSelectionTuiTests --no-restore
```

实际 RED：2 失败、1 通过；第三面板会话重复绑定成 `[a,c,c]`，跨项目选择未切换项目。实际 GREEN：3/3 通过；复用可见 pane 并保留原项目布局。

其余新增行为均有模块记录中的失败命令、预期原因和通过证据。文档、帮助文案、发布清单属于说明或打包配置，使用实际构建、帮助输出与包内容检查，不制造镜像字符串测试。

## 必须区分的验证边界

- 原生恢复和 hook 使用真实本地 fixture 进程、真实 ConPTY、真实 exe 和命名管道验证；不是模拟的函数返回值。但没有向付费模型发送提示，因此**尚未验证真实 Claude/Codex 模型服务的历史对话回读及真实回合完成回调**。
- 本机 Claude Code 2.1.310、Codex CLI 0.163.0 已检查 help/version 和交互启动画面；没有替用户批准工具、更新 CLI 或绕过 trust。Gemini/OpenCode/Copilot 的适配基于官方参数和 fixture，未声称本机已实际运行。
- 已有 notify/调用级设置会保留；不能自动连接时状态说明给出原因，可用 `session-bind` / `agent-report` 显式接入。未捕获原生 ID 时不会用“最近会话”冒充准确续接。
- 终端支持常用 VT 子集，仍有复杂 emoji/字形、旧鼠标编码与 Windows 输入代码页的限制；不支持终端图像协议。详见终端验证记录。
- 用户原有默认状态目录的旧 daemon（检查时 PID 18160）保持运行。新版显示 OLD SERVER，并阻止旧服务不支持的新命令；用户处理好原工作后才能显式 `stop` 并重开。验收均使用独立临时目录。

工作区包含同期已有修改，本轮没有自动创建混合提交或清理他人改动。最终测试针对当前完整工作区与实际发布产物执行。
