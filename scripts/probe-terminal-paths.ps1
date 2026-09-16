#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $root "tools\tui-matrix"
$probe = Join-Path $toolDirectory "terminal-probe.mjs"
$launcherProject = Join-Path $root "src\Magpilot.Host\Magpilot.Host.csproj"
$launcher = Join-Path $root "src\Magpilot.Host\bin\Release\net10.0\magpilot.exe"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $toolDirectory "captures\terminal-probe-$stamp"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

if (-not $SkipBuild) {
    dotnet build $launcherProject -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Magpilot.Host build failed with exit code $LASTEXITCODE."
    }
}
if (-not (Test-Path $launcher)) {
    throw "Local launcher not found: $launcher"
}

$node = (Get-Command node -ErrorAction Stop).Source
$directPath = Join-Path $OutputDirectory "direct.json"
$diffPath = Join-Path $OutputDirectory "comparison.json"

$controlledVariables = @(
    "MAGPILOT_AGENT_TOKEN",
    "MAGPILOT_REAL_COPILOT",
    "MAGPILOT_TERM_MANIFEST",
    "MAGPILOT_TERMINAL_PROBE_OUTPUT",
    "MAGPILOT_CONSOLE_MODE_HELPER",
    "MAGPILOT_CONPTY"
)
$previousEnvironment = @{}
foreach ($variable in $controlledVariables) {
    $previousEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable)
}

try {
    Remove-Item Env:MAGPILOT_AGENT_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:MAGPILOT_TERM_MANIFEST -ErrorAction SilentlyContinue
    $env:MAGPILOT_CONSOLE_MODE_HELPER = $launcher
    $env:MAGPILOT_TERMINAL_PROBE_OUTPUT = $directPath

    Write-Host "Probing direct terminal path..." -ForegroundColor Cyan
    & $node $probe
    if ($LASTEXITCODE -ne 0) {
        throw "Direct terminal probe failed with exit code $LASTEXITCODE."
    }

    $env:MAGPILOT_REAL_COPILOT = $node
    foreach ($implementation in @("app-local", "system")) {
        $env:MAGPILOT_CONPTY = $implementation
        $env:MAGPILOT_TERM_MANIFEST = Join-Path $OutputDirectory "nested-$implementation-manifest.json"
        $env:MAGPILOT_TERMINAL_PROBE_OUTPUT = Join-Path $OutputDirectory "nested-$implementation.json"

        Write-Host "Probing nested Magpilot ConPTY path ($implementation)..." -ForegroundColor Cyan
        & $launcher "--magpilot-tui-options=none" $probe
        if ($LASTEXITCODE -ne 0) {
            throw "Nested terminal probe ($implementation) failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    foreach ($variable in $controlledVariables) {
        [Environment]::SetEnvironmentVariable(
            $variable,
            $previousEnvironment[$variable],
            [EnvironmentVariableTarget]::Process)
    }
}

$direct = Get-Content $directPath -Raw | ConvertFrom-Json
$nestedResults = [ordered]@{}
foreach ($implementation in @("app-local", "system")) {
    $nestedResults[$implementation] = Get-Content `
        (Join-Path $OutputDirectory "nested-$implementation.json") `
        -Raw | ConvertFrom-Json
}

$differences = [Collections.Generic.List[object]]::new()
foreach ($implementation in $nestedResults.Keys) {
    $nested = $nestedResults[$implementation]
    $replyDifferences = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $direct.replies.Count; $index++) {
        $replyDifferences.Add([ordered]@{
            name = $direct.replies[$index].name
            directBytes = $direct.replies[$index].responseBytes
            nestedBytes = $nested.replies[$index].responseBytes
            directResponse = $direct.replies[$index].responseDisplay
            nestedResponse = $nested.replies[$index].responseDisplay
        })
    }

    $differences.Add([ordered]@{
        implementation = $implementation
        stdinIsTTY = @($direct.process.stdinIsTTY, $nested.process.stdinIsTTY)
        stdoutIsTTY = @($direct.process.stdoutIsTTY, $nested.process.stdoutIsTTY)
        columns = @($direct.process.columns, $nested.process.columns)
        rows = @($direct.process.rows, $nested.process.rows)
        colorDepth = @($direct.process.colorDepth, $nested.process.colorDepth)
        hasColors16 = @($direct.process.hasColors16, $nested.process.hasColors16)
        hasColors256 = @($direct.process.hasColors256, $nested.process.hasColors256)
        hasColorsTrueColor = @($direct.process.hasColorsTrueColor, $nested.process.hasColorsTrueColor)
        stdinHandleType = @($direct.process.stdinHandleType, $nested.process.stdinHandleType)
        stdoutHandleType = @($direct.process.stdoutHandleType, $nested.process.stdoutHandleType)
        inputMode = @($direct.consoleMode.InputMode, $nested.consoleMode.InputMode)
        outputMode = @($direct.consoleMode.OutputMode, $nested.consoleMode.OutputMode)
        virtualTerminalInput = @(
            $direct.consoleMode.VirtualTerminalInput,
            $nested.consoleMode.VirtualTerminalInput)
        virtualTerminalOutput = @(
            $direct.consoleMode.VirtualTerminalOutput,
            $nested.consoleMode.VirtualTerminalOutput)
        replies = $replyDifferences
    })
}

$comparison = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow
    direct = $direct
    nestedPty = $nestedResults
    differences = $differences
}
$comparison | ConvertTo-Json -Depth 12 | Set-Content -Path $diffPath -Encoding utf8

Write-Host ""
Write-Host "TTY capabilities" -ForegroundColor Green
$ttyRows = [Collections.Generic.List[object]]::new()
$ttyRows.Add([pscustomobject]@{
    Path = "direct"
    StdinTTY = $direct.process.stdinIsTTY
    StdoutTTY = $direct.process.stdoutIsTTY
    Size = "$($direct.process.columns)x$($direct.process.rows)"
    ColorDepth = $direct.process.colorDepth
    Colors256 = $direct.process.hasColors256
    TrueColor = $direct.process.hasColorsTrueColor
    InputMode = $direct.consoleMode.InputMode
    VTInput = $direct.consoleMode.VirtualTerminalInput
    OutputMode = $direct.consoleMode.OutputMode
    VTOutput = $direct.consoleMode.VirtualTerminalOutput
})
foreach ($implementation in $nestedResults.Keys) {
    $nested = $nestedResults[$implementation]
    $ttyRows.Add([pscustomobject]@{
        Path = "nested-$implementation"
        StdinTTY = $nested.process.stdinIsTTY
        StdoutTTY = $nested.process.stdoutIsTTY
        Size = "$($nested.process.columns)x$($nested.process.rows)"
        ColorDepth = $nested.process.colorDepth
        Colors256 = $nested.process.hasColors256
        TrueColor = $nested.process.hasColorsTrueColor
        InputMode = $nested.consoleMode.InputMode
        VTInput = $nested.consoleMode.VirtualTerminalInput
        OutputMode = $nested.consoleMode.OutputMode
        VTOutput = $nested.consoleMode.VirtualTerminalOutput
    })
}
$ttyRows | Format-Table -AutoSize

Write-Host "Terminal reply bytes" -ForegroundColor Green
$replyRows = [Collections.Generic.List[object]]::new()
foreach ($implementation in $nestedResults.Keys) {
    $nested = $nestedResults[$implementation]
    for ($index = 0; $index -lt $direct.replies.Count; $index++) {
        $replyRows.Add([pscustomobject]@{
            Implementation = $implementation
            QueryGroup = $direct.replies[$index].name
            Direct = $direct.replies[$index].responseBytes
            NestedPty = $nested.replies[$index].responseBytes
        })
    }
}
$replyRows | Format-Table -AutoSize

if (-not $direct.process.stdoutIsTTY) {
    Write-Warning "The direct probe did not see a TTY. Run this script from the interactive Git Bash window where you normally launch Copilot."
}

Write-Host "Probe results: $OutputDirectory" -ForegroundColor Green
Write-Host "Comparison: $diffPath"
