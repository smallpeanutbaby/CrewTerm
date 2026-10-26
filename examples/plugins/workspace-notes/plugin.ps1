param([Parameter(Mandatory)][ValidateSet('summarize', 'record-event')][string]$Action)

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$raw = [Console]::In.ReadToEnd()
$request = $raw | ConvertFrom-Json
if ($request.schemaVersion -ne 1) { throw 'Unsupported CrewTerm plugin envelope version.' }

switch ($Action) {
    'summarize' {
        $workspace = $request.payload.workingDirectory
        if ([string]::IsNullOrWhiteSpace($workspace)) { $workspace = $request.payload.projectRoot }
        if ([string]::IsNullOrWhiteSpace($workspace)) { throw 'payload.workingDirectory or payload.projectRoot is required.' }
        if (-not [IO.Directory]::Exists($workspace)) { throw 'The workspace directory does not exist.' }
        $entries = @(Get-ChildItem -LiteralPath $workspace -Force | Select-Object -First 1001)
        $limited = @($entries | Select-Object -First 1000)
        $report = @{
            workingDirectory = [IO.Path]::GetFullPath($workspace)
            files = @($limited | Where-Object { -not $_.PSIsContainer }).Count
            directories = @($limited | Where-Object { $_.PSIsContainer }).Count
            truncated = $entries.Count -gt 1000
            names = @($limited | Sort-Object Name | Select-Object -First 20 -ExpandProperty Name)
        }
        [Console]::Out.Write(($report | ConvertTo-Json -Compress))
    }
    'record-event' {
        if ($request.kind -ne 'event') { throw 'This command requires a CrewTerm event envelope.' }
        $directory = Join-Path $PSScriptRoot 'events'
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $name = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N') + '.json'
        $path = Join-Path $directory $name
        [IO.File]::WriteAllText($path, $raw, [Text.UTF8Encoding]::new($false))
        [Console]::Out.Write((@{ eventName = $request.eventName; savedTo = $path } | ConvertTo-Json -Compress))
    }
}
