# 贡献指南

感谢你愿意改进 CrewTerm。本文说明开发环境、工程规范与提交要求；构建、调试、测试体系与常见改动的完整步骤见
[开发者指南](docs/development.md)。参与本项目即表示你同意遵守 [行为准则](CODE_OF_CONDUCT.md)。

## 报告问题

- 先在 [Issues](https://github.com/smallpeanutbaby/CrewTerm/issues) 中搜索是否已有相同问题，再使用“问题报告”或“功能建议”模板提交。
- 请附上：操作系统与版本、终端程序、`crewterm --version` 的输出、重现步骤、期望结果与实际结果；
  后台服务的问题请附上状态目录中 `server-error.log` 的相关内容（先删除其中的敏感信息）。
- 安全问题请不要公开提交，按 [安全策略](SECURITY.md) 私下报告。

## 开发环境

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（版本见 `global.json`）。
- Git。
- Linux / macOS 上运行全部测试需要 [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)（`pwsh`），
  部分测试用它模拟 Agent CLI；Windows 上没有 `pwsh` 时自动使用系统自带的 Windows PowerShell。

```sh
dotnet build CrewTerm.slnx -c Release
dotnet test  CrewTerm.slnx -c Release
```

正在运行的 CrewTerm 后台服务会锁定 `bin/Debug` 中的程序集，开发时请使用 `-c Release`，或先运行 `crewterm stop`。

## 工程规范

构建会强制检查下列规则（`Directory.Build.props` 与 `.editorconfig`），任何违反都会使构建失败：

- **警告即错误**，包括平台兼容性分析（CA1416）：调用只在某个平台可用的 API 时，必须用 `OperatingSystem.IsWindows()` 等判断保护，
  或在类型、成员上标注 `[SupportedOSPlatform]`。
- **XML 文档注释**：所有类型与成员（包括私有成员与测试）都要有中文注释；`<summary>` 写成多行，参数、返回值与异常完整说明。
- **一个文件只声明一个类型**，**命名空间与文件夹一致**，不保留未使用的 `using`。
- **依赖只能向下**：Core 不依赖任何项目；Terminal、Workspace、Automation 互不依赖；只有 App 组合全部模块。
  App 内部 `UI`、`Cli` → `Client` → `Protocol` ← `Server`。详见 [架构文档](docs/architecture.md)。

## 测试

- 新功能与缺陷修复都需要测试。优先使用真实进程、真实文件与真实伪终端，而不是模拟对象。
- 跨平台行为写成普通的 `[Fact]` / `[Theory]`，用 `CrewTerm.Testing` 中的 `TestShell` 获取当前平台的 Shell 命令与伪造程序。
- 只在某个平台上成立的行为使用 `[WindowsFact]`、`[WindowsTheory]`、`[UnixFact]`、`[UnixTheory]`，不要在测试中用提前返回跳过。
- 等待异步结果使用 `Poll.Until` 之类的条件等待，不要依赖固定延时。
- 推送后，持续集成会在 Windows、Ubuntu 与 macOS 上运行全部测试；请确保三个平台都通过。

## 提交

- 从 `main` 创建分支，一个拉取请求只做一件事。
- 提交信息采用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/) 格式，例如 `feat: ...`、`fix: ...`、`refactor: ...`、`docs: ...`。
- 提交前运行 `./scripts/verify.sh`（或 `./scripts/verify.ps1`），完成构建、测试与空白字符检查。
- 用户可见的变化请同时更新 [CHANGELOG.md](CHANGELOG.md) 的“未发布”部分与相关文档。

## 发布

维护者在 `main` 上更新版本号（`src/CrewTerm.App/CrewTerm.App.csproj`）与 CHANGELOG 后推送 `vX.Y.Z` 标签；
发布流程会先在三个平台上运行全部测试，通过后生成各平台的独立发布包并创建 GitHub Release。

## 许可

提交贡献即表示你同意以 [MIT 许可证](LICENSE) 发布你的贡献。
