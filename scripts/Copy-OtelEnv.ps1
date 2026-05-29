# Copy-OtelEnv.ps1 — Copy OTEL env var block to clipboard (paste into any shell before launching Claude)
$envFile = "$env:USERPROFILE\.derivity\runtime-meter\claude-code-env.ps1"

if (-not (Test-Path $envFile)) {
    Write-Error "claude-code-env.ps1 not found. Start DerivityMeter first."
    exit 1
}

Get-Content $envFile -Raw | Set-Clipboard
Write-Host "OTEL env vars copied to clipboard. Paste into your shell then run: claude"
