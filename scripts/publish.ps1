$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet publish src/CrewTerm.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/win-x64/README.md
    New-Item -ItemType Directory -Path artifacts/win-x64/docs -Force | Out-Null
    Copy-Item -LiteralPath docs/cli-guide.md -Destination artifacts/win-x64/docs/cli-guide.md
    Get-ChildItem -LiteralPath docs -Filter 'verification-*.md' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path 'artifacts/win-x64/docs' $_.Name)
    }
    $launcher = @('@echo off', 'setlocal', '"%~dp0crewterm.exe" %*', 'if errorlevel 1 pause') -join "`r`n"
    [IO.File]::WriteAllText((Join-Path (Get-Location) 'artifacts/win-x64/Start CrewTerm.cmd'), $launcher + "`r`n")
    Write-Host 'Ready: artifacts/win-x64/crewterm.exe'
} finally { Pop-Location }
