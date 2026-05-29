# Show-Status.ps1 — Print current DerivityMeter runtime status
$statusFile = "$env:USERPROFILE\.derivity\runtime-meter\runtime-status.json"

if (-not (Test-Path $statusFile)) {
    Write-Host "DerivityMeter: not running (no status file)"
    exit 0
}

$s = Get-Content $statusFile -Raw | ConvertFrom-Json

$mcpState  = if ($s.Mcp.Running)  { "running  $($s.Mcp.Url)"  } else { "offline" }
$otelState = if ($s.Otel.Running) { "running  $($s.Otel.Url)" } else { "offline" }

Write-Host "MCP:   $mcpState"
Write-Host "OTEL:  $otelState"
