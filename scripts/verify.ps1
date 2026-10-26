$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet test CrewTerm.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet build CrewTerm.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
} finally { Pop-Location }
