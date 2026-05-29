# Start-Meter.ps1 — Launch DerivityMeter (or bring existing instance to front)
$exe = "$PSScriptRoot\..\publish\DerivityMeter.exe"
Start-Process -FilePath (Resolve-Path $exe)
