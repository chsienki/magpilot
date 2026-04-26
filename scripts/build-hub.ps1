#!/usr/bin/env pwsh
# Build script: publishes Clawpilot.Web (Blazor WASM) and copies the bundle
# into Clawpilot.Hub/wwwroot so the hub serves the SPA at /.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "Publishing Clawpilot.Web..."
    dotnet publish "src/Clawpilot.Web/Clawpilot.Web.csproj" -c Release --nologo -v:q

    $src = "src/Clawpilot.Web/bin/Release/net9.0/publish/wwwroot"
    $dst = "src/Clawpilot.Hub/wwwroot"
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Path $dst | Out-Null
    Copy-Item -Recurse -Force "$src/*" $dst
    Write-Host "SPA copied to $dst"

    Write-Host "Publishing Clawpilot.Hub..."
    dotnet publish "src/Clawpilot.Hub/Clawpilot.Hub.csproj" -c Release --nologo -v:q
}
finally { Pop-Location }
