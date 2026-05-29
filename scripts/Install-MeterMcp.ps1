# Install-MeterMcp.ps1 — Register or remove DerivityMeter as a Claude Code MCP server (user scope)
# Modifies: %USERPROFILE%\.claude\settings.json  (user scope)
# Usage:
#   powershell -File Install-MeterMcp.ps1           # install
#   powershell -File Install-MeterMcp.ps1 -Uninstall # remove
param([switch]$Uninstall)

$settingsFile = "$env:USERPROFILE\.claude\settings.json"

if ($Uninstall) {
    Write-Host "Removing derivity-meter MCP server..."
    claude mcp remove --scope user derivity-meter
    Write-Host "Done. Restart Claude Code for the change to take effect."
    exit 0
}

# Backup settings.json before modifying
if (Test-Path $settingsFile) {
    $backup = "$settingsFile.bak"
    Copy-Item $settingsFile $backup -Force
    Write-Host "Backed up settings.json -> settings.json.bak"
}

$bridge = Resolve-Path "$PSScriptRoot\mcp-bridge.mjs"

Write-Host "Registering derivity-meter MCP server..."
claude mcp add --scope user derivity-meter -- node "$bridge"

Write-Host ""
Write-Host "Done. Restart Claude Code for the server to load."
Write-Host "Verify with:   claude mcp get derivity-meter"
Write-Host "To uninstall:  powershell -File Install-MeterMcp.ps1 -Uninstall"
