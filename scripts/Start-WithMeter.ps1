# Start-WithMeter.ps1 — Launch DerivityMeter, wait for OTEL, set env vars, then launch Claude Code
param([Parameter(ValueFromRemainingArguments)][string[]]$ClaudeArgs)

$exe        = "$PSScriptRoot\..\publish\DerivityMeter.exe"
$statusFile = "$env:USERPROFILE\.derivity\runtime-meter\runtime-status.json"
$envFile    = "$env:USERPROFILE\.derivity\runtime-meter\claude-code-env.ps1"

Start-Process -FilePath (Resolve-Path $exe)

# Wait up to 15s for OTEL to come up
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    if (Test-Path $statusFile) {
        $s = Get-Content $statusFile -Raw | ConvertFrom-Json
        if ($s.Otel.Running -eq $true) { $ready = $true; break }
    }
}

if (-not $ready) {
    Write-Warning "DerivityMeter OTEL did not confirm ready in 15s — launching Claude without telemetry env vars."
} elseif (Test-Path $envFile) {
    . $envFile
}

& claude @ClaudeArgs
