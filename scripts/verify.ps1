# 提交前的完整检查：Release 构建（警告即错误）、全部测试、空白字符检查。
# Windows PowerShell 5.1 与 PowerShell 7（三个平台）均可运行；Linux / macOS 也可以使用 scripts/verify.sh。
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet build CrewTerm.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test CrewTerm.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
} finally { Pop-Location }
