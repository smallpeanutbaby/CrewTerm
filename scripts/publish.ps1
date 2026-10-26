# 生成自带 .NET 运行时的独立发布包，输出到 artifacts/<RID>。
# 用法：./scripts/publish.ps1 [-Runtime win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64]
# 不指定 -Runtime 时按当前系统与处理器架构选择。Windows PowerShell 5.1 与 PowerShell 7（三个平台）均可运行。
param([string]$Runtime = '')

$ErrorActionPreference = 'Stop'
$onWindows = $env:OS -eq 'Windows_NT'
if (-not $Runtime) {
    $os = if ($onWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Runtime = "$os-$arch"
}
$output = "artifacts/$Runtime"
$executable = if ($Runtime.StartsWith('win-')) { 'crewterm.exe' } else { 'crewterm' }

Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet publish src/CrewTerm.App -c Release -r $Runtime --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    foreach ($file in 'README.md', 'LICENSE', 'CHANGELOG.md') {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $output $file) }
    }
    New-Item -ItemType Directory -Path "$output/docs" -Force | Out-Null
    Get-ChildItem -LiteralPath docs -Filter '*.md' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path "$output/docs" $_.Name)
    }
    New-Item -ItemType Directory -Path "$output/examples/plugins" -Force | Out-Null
    Copy-Item -LiteralPath examples/plugins/workspace-notes -Destination "$output/examples/plugins" -Recurse -Force
    if ($Runtime.StartsWith('win-')) {
        # 双击启动的入口：出错退出时暂停，便于看到错误信息。
        $launcher = @('@echo off', 'setlocal', '"%~dp0crewterm.exe" %*', 'if errorlevel 1 pause') -join "`r`n"
        [IO.File]::WriteAllText((Join-Path (Get-Location) "$output/Start CrewTerm.cmd"), $launcher + "`r`n")
    }
    Write-Host "Ready: $output/$executable"
} finally { Pop-Location }
