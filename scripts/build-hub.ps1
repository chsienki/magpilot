#!/usr/bin/env pwsh
# Build script: publishes Magpilot.Web (Blazor WASM) and copies the bundle
# into Magpilot.Hub/wwwroot so the hub serves the SPA at /.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "Publishing Magpilot.Web..."
    dotnet publish "src/Magpilot.Web/Magpilot.Web.csproj" -c Release --nologo -v:q

    $src = "src/Magpilot.Web/bin/Release/net9.0/publish/wwwroot"
    $dst = "src/Magpilot.Hub/wwwroot"
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Path $dst | Out-Null
    Copy-Item -Recurse -Force "$src/*" $dst
    Write-Host "SPA copied to $dst"

    Write-Host "Publishing Magpilot.Hub..."
    dotnet publish "src/Magpilot.Hub/Magpilot.Hub.csproj" -c Release --nologo -v:q
}
finally { Pop-Location }
