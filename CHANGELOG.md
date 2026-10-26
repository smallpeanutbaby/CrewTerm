# 更新日志

本项目的所有重要变更都记录在此文件中。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。1.0 之前的次版本号变化可能包含不兼容的改动。

## [0.3.0] - 未发布

### 新增

- **支持 Linux 与 macOS**：新增基于 `forkpty` 的伪终端 `UnixPtySession`（作业控制、`SIGWINCH`、进程组结束、退出码 128 + 信号），
  与 Windows ConPTY 共用 `ITerminalSession` 接口，由 `TerminalSession.Start` 按平台选择实现。
- Unix 上的后台服务：启动后创建新会话并脱离启动它的终端；监听状态目录中的 Unix 域套接字 `server.sock`。
- 三平台持续集成：每次推送在 Windows、Ubuntu、macOS（Apple Silicon）上构建并运行全部测试，失败的测试以注释显示在检查结果中。
- 发布流程：推送 `v*` 标签后，测试通过即为 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`
  生成自带运行时的发布包并创建 GitHub Release。
- `scripts/publish.sh`、`scripts/verify.sh`；`scripts/publish.ps1` 新增 `-Runtime` 参数。
- 共享测试支持项目 `CrewTerm.Testing`：平台限定的测试特性、按平台选择的测试 Shell 与伪造程序、统一的可执行文件定位。
- MIT 许可证、贡献指南与安全策略。
- 完整文档：[文档首页](docs/README.md)、快速入门、使用手册、快捷键与命令参考、Agent 集成指南、多 Agent 工作流实战、
  配置与数据参考、后台服务协议、开发者指南、常见问题与术语表；本地插件说明扩写为完整的开发指南。
- 行为准则、Issue 模板（问题报告、功能建议）与拉取请求模板。

### 变更

- `ConPtySession` 标记为仅 Windows；跨平台代码通过 `ITerminalSession` 使用终端会话。
- 新建自定义终端的默认命令：Windows 为 `cmd.exe`，Linux / macOS 为 `$SHELL`（未设置时为 `/bin/sh`）。
- `ExecutableLocator` 在 Linux / macOS 上只接受有执行权限的文件。
- 各版本的验证记录移到 `docs/verification/`，发布包中的 `docs/` 只包含用户文档。
- `crewterm --help` 与 F1 帮助的标题列出三个支持的平台；F1 帮助中发送保留键的命令名与命令面板一致；
  插件清单查看器在 Linux / macOS 上不再提示“Windows 用户”。
- 确认框（终止会话、停止全部、多行粘贴、删除 worktree、恢复检查点等）的默认按钮改为 `Cancel`：直接按 Enter 或 Esc 都是取消，
  确认需要按 Alt+O 或点击 `Confirm`；Alt+C 与 `Unsaved changes` 中一样表示取消。
- Windows 上的后台任务、命令模板与流水线不再执行项目目录中与裸程序名同名的脚本，项目内的脚本需写成 `.\build.cmd`（与终端会话一致）。
- `ExecutableLocator` 移到 `CrewTerm.Core.IO`，Agent 发现与 Git 调用共用同一套查找规则。

### 安全

- Linux / macOS：裸程序名只在 `PATH` 的绝对路径条目中查找，忽略 `.` 与空条目；伪终端主设备设置 close-on-exec，
  不会被其他会话的进程继承；新建的状态目录权限为 `0700`，后台服务的套接字位于其中，其他用户无法连接或抢占。
- Linux / macOS：创建伪终端子进程期间屏蔽信号，`exec` 之前不会运行继承来的运行时信号处理程序。
- Linux / macOS：含 Shell 展开语法（`$`、反引号、反斜杠等）的路径不会嵌入 Provider hook 命令，
  含这类语法的启动命令需要显式登记恢复命令，不会被自动改写。
- Git 先在 `PATH` 的绝对路径条目中解析出绝对路径再启动（Windows 上只接受 `git.exe`）。此前以裸程序名 `git` 启动，
  Windows 的 `CreateProcess` 与 .NET 在 Unix 上的解析都会先查找工作台进程的当前目录：在不受信任的仓库中启动 CrewTerm 时，
  仓库中的同名程序可能被执行。
- Windows：后台任务、命令模板与流水线的 cmd 设置 `NoDefaultCurrentDirectoryInExePath=1`，不再先在项目目录中查找裸程序名，
  项目中的 `dotnet.bat`、`chcp.bat` 等无法劫持任务命令。
- Linux / macOS：所有入口（包括 `crewterm doctor` 与工作台第一次保存设置）新建的状态目录都是 `0700`，此前部分入口按 umask 创建。

### 修复

- 滚动历史查看器与编辑器中的 Ctrl+F 打开查找框（此前没有反应）。
- 在收件箱或通知列表中再次打开已读的消息或通知时不再报错。
- macOS：项目位于符号链接之下（例如 `/var` → `/private/var`）时，Git 改动的相对路径与 worktree 的创建、删除不再出错。

## [0.2.0] - 2026-10-26

### 新增

- 命名标签页、四向嵌套分屏与按项目保存的布局；鼠标选择、复制粘贴、右键菜单与滚动历史。
- Agent 语义状态、通知、定向收件箱、任务交接与状态等待；Claude Code 与 Codex 的单次启动生命周期回调。
- 原生会话目录：记录 Provider 的精确会话标识，支持恢复、重新开始与遗忘。
- 本地插件（JSON 清单、命令与事件钩子，默认停用）、文件检查点、后台任务流水线。

### 变更

- 按层组织全部类库（每个项目按职责分文件夹），一个文件只声明一个类型，命名空间与文件夹一致。
- 所有类型与成员都有中文 XML 文档注释；构建强制执行注释、未使用 `using` 与命名空间规则。

### 安全

- 终端命令不在项目目录中查找程序；粘贴与提示词去除控制字符；显示文本清理终端转义序列；后台服务只接受绝对路径。

## [0.1.0] - 2026-10-26

### 新增

- Windows 原生多 Agent 终端工作台：ConPTY 会话、后台服务、项目与文件浏览、代码编辑、Git 改动查看、后台任务与命令行接口。

[0.3.0]: https://github.com/smallpeanutbaby/CrewTerm/compare/e7bde45...HEAD
[0.2.0]: https://github.com/smallpeanutbaby/CrewTerm/compare/b55e8e5...e7bde45
[0.1.0]: https://github.com/smallpeanutbaby/CrewTerm/commit/b55e8e5
