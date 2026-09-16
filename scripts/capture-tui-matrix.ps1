#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [ValidateSet("Hints", "Rendering", "Transport", "All")]
    [string] $Phase = "Hints",

    [string[]] $Case,

    [ValidateRange(250, 30000)]
    [int] $CaptureMilliseconds = 3000,

    [string] $OutputDirectory,

    [string] $ThemeFile,

    [switch] $ListCases,

    [switch] $SkipBuild,

    [switch] $SkipRender
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $root "tools\tui-matrix"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $toolDirectory "captures\$stamp"
}

$launcherProject = Join-Path $root "src\Magpilot.Host\Magpilot.Host.csproj"
$launcher = Join-Path $root "src\Magpilot.Host\bin\Release\net10.0\magpilot.exe"

function New-MatrixCase {
    param(
        [string] $Name,
        [string] $Options,
        [ValidateSet("app-local", "system")]
        [string] $ConPty = "app-local",
        [ValidateSet("auto", "dark", "light")]
        [string] $Background = "auto"
    )

    [pscustomobject]@{
        Name = $Name
        Options = $Options
        ConPty = $ConPty
        Background = $Background
    }
}

$hintFeatures = @("term", "truecolor", "background", "github-theme")
$hintCases = for ($mask = 0; $mask -lt (1 -shl $hintFeatures.Count); $mask++) {
    $enabled = for ($index = 0; $index -lt $hintFeatures.Count; $index++) {
        if (($mask -band (1 -shl $index)) -ne 0) {
            $hintFeatures[$index]
        }
    }

    $name = if ($enabled.Count -eq 0) { "none" } else { $enabled -join "+" }
    $options = if ($enabled.Count -eq 0) { "none" } else { $enabled -join "," }
    New-MatrixCase -Name $name -Options $options
}

$hintCases += @(
    (New-MatrixCase -Name "background-dark" -Options "background" -Background "dark"),
    (New-MatrixCase -Name "background-light" -Options "background" -Background "light")
)

$renderingCases = @(
    (New-MatrixCase -Name "palette" -Options "palette"),
    (New-MatrixCase -Name "thinking" -Options "thinking"),
    (New-MatrixCase -Name "input-band" -Options "input-band"),
    (New-MatrixCase -Name "legacy-colors" -Options "legacy-colors"),
    (New-MatrixCase -Name "rewrite" -Options "rewrite"),
    (New-MatrixCase -Name "banner" -Options "banner"),
    (New-MatrixCase -Name "palette+thinking" -Options "palette,thinking"),
    (New-MatrixCase -Name "palette+input-band" -Options "palette,input-band"),
    (New-MatrixCase -Name "palette+legacy-colors" -Options "palette,legacy-colors"),
    (New-MatrixCase -Name "palette+rewrite" -Options "palette,rewrite"),
    (New-MatrixCase -Name "all" -Options "all")
)

$transportCases = @(
    (New-MatrixCase -Name "none" -Options "none"),
    (New-MatrixCase -Name "system-none" -Options "none" -ConPty "system")
)

$cases = switch ($Phase) {
    "Hints" { $hintCases }
    "Rendering" { @((New-MatrixCase -Name "none" -Options "none")) + @($renderingCases) }
    "Transport" { $transportCases }
    "All" {
        @($hintCases) +
            @($renderingCases) +
            @($transportCases | Where-Object Name -NE "none")
    }
}

$requestedCases = @(
    $Case |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 }
)

if ($requestedCases.Count -gt 0) {
    $cases = @($cases | Where-Object { $requestedCases -contains $_.Name })
    $missing = @($requestedCases | Where-Object {
        $name = $_
        -not ($cases | Where-Object Name -EQ $name)
    })
    if ($missing.Count -gt 0) {
        throw "Unknown case(s): $($missing -join ', ')."
    }
}

if ($ListCases) {
    $cases | Format-Table Name, Options, ConPty, Background
    return
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

function Read-EnvFile {
    param([string] $Path)

    $values = @{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    foreach ($line in Get-Content $Path) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $values[$matches[1]] = $matches[2]
        }
    }
    return $values
}

if ([string]::IsNullOrWhiteSpace($ThemeFile)) {
    $ThemeFile = [Environment]::GetEnvironmentVariable("MAGPILOT_TERM_THEME_FILE")
}

if ([string]::IsNullOrWhiteSpace($ThemeFile)) {
    $themeName = [Environment]::GetEnvironmentVariable("MAGPILOT_TERM_THEME")
    if (-not [string]::IsNullOrWhiteSpace($themeName)) {
        $themeCandidates = @(
            (Join-Path $HOME ".config\magpilot\themes\$themeName.json"),
            (Join-Path $env:ProgramFiles "Magpilot\config\themes\$themeName.json")
        )
        $ThemeFile = $themeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($ThemeFile)) {
    $installedConfig = Join-Path $env:ProgramFiles "Magpilot\config"
    $installedValues = Read-EnvFile (Join-Path $installedConfig "magpilot.env")
    if ($installedValues.ContainsKey("MAGPILOT_TERM_THEME_FILE")) {
        $ThemeFile = $installedValues["MAGPILOT_TERM_THEME_FILE"]
    }
    elseif ($installedValues.ContainsKey("MAGPILOT_TERM_THEME")) {
        $ThemeFile = Join-Path $installedConfig "themes\$($installedValues["MAGPILOT_TERM_THEME"]).json"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ThemeFile)) {
    $ThemeFile = [IO.Path]::GetFullPath($ThemeFile)
    if (-not (Test-Path $ThemeFile)) {
        throw "Theme file not found: $ThemeFile"
    }
}
elseif ($Phase -in @("Rendering", "All")) {
    Write-Warning "No installed or explicit theme file was found. Palette and rewrite cases will intentionally be no-ops."
}

$controlledVariables = @(
    "TERM",
    "COLORTERM",
    "COLORFGBG",
    "COPILOT_GITHUB_THEME",
    "MAGPILOT_AGENT_TOKEN",
    "MAGPILOT_TERM_BACKGROUND",
    "MAGPILOT_TERM_ENABLE_GITHUB_THEME",
    "MAGPILOT_TERM_THEME",
    "MAGPILOT_TERM_THEME_FILE",
    "MAGPILOT_TERM_BANNER_TAG",
    "MAGPILOT_TERM_DUMP",
    "MAGPILOT_TERM_DUMP_POST",
    "MAGPILOT_TERM_DUMP_MS",
    "MAGPILOT_TERM_MANIFEST",
    "MAGPILOT_CONPTY"
)
$previousEnvironment = @{}
foreach ($variable in $controlledVariables) {
    $previousEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable)
}

$results = [Collections.Generic.List[object]]::new()
try {
    foreach ($variable in @("TERM", "COLORTERM", "COLORFGBG", "COPILOT_GITHUB_THEME", "MAGPILOT_AGENT_TOKEN")) {
        Remove-Item "Env:$variable" -ErrorAction SilentlyContinue
    }

    $env:MAGPILOT_TERM_ENABLE_GITHUB_THEME = "1"
    Remove-Item Env:MAGPILOT_TERM_THEME -ErrorAction SilentlyContinue
    Remove-Item Env:MAGPILOT_TERM_BANNER_TAG -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($ThemeFile)) {
        Remove-Item Env:MAGPILOT_TERM_THEME_FILE -ErrorAction SilentlyContinue
    }
    else {
        $env:MAGPILOT_TERM_THEME_FILE = $ThemeFile
    }

    for ($index = 0; $index -lt $cases.Count; $index++) {
        $matrixCase = $cases[$index]
        $caseDirectory = Join-Path $OutputDirectory $matrixCase.Name
        New-Item -ItemType Directory -Path $caseDirectory | Out-Null

        $rawDump = Join-Path $caseDirectory "raw.ansi"
        $postDump = Join-Path $caseDirectory "post.ansi"
        $manifest = Join-Path $caseDirectory "manifest.json"

        $env:MAGPILOT_TERM_BACKGROUND = $matrixCase.Background
        $env:MAGPILOT_CONPTY = $matrixCase.ConPty
        $env:MAGPILOT_TERM_DUMP = $rawDump
        $env:MAGPILOT_TERM_DUMP_POST = $postDump
        $env:MAGPILOT_TERM_DUMP_MS = $CaptureMilliseconds.ToString()
        $env:MAGPILOT_TERM_MANIFEST = $manifest

        Write-Host ""
        Write-Host "[$($index + 1)/$($cases.Count)] $($matrixCase.Name)" -ForegroundColor Cyan
        Write-Host "  options:    $($matrixCase.Options)"
        Write-Host "  ConPTY:     $($matrixCase.ConPty)"
        Write-Host "  background: $($matrixCase.Background)"
        Write-Host "  capture:    $CaptureMilliseconds ms"
        Write-Host "Wait for the welcome screen to settle, note what changed, then type /exit."
        Read-Host "Press Enter to launch" | Out-Null

        & $launcher "--magpilot-tui-options=$($matrixCase.Options)"
        $exitCode = $LASTEXITCODE

        foreach ($required in @($manifest, $rawDump, $postDump)) {
            if (-not (Test-Path $required)) {
                throw "Case '$($matrixCase.Name)' did not produce $required."
            }
        }

        $rawInfo = Get-Item $rawDump
        $postInfo = Get-Item $postDump
        $resolved = Get-Content $manifest -Raw | ConvertFrom-Json
        $results.Add([pscustomobject]@{
            Case = $matrixCase.Name
            Options = $matrixCase.Options
            ConPty = $resolved.ConPtyImplementation
            Background = $matrixCase.Background
            Term = $resolved.Term
            ColorTerm = $resolved.ColorTerm
            ColorFgBg = $resolved.ColorFgBg
            CopilotGithubTheme = $resolved.CopilotGithubTheme
            DetectedBackground = $resolved.DetectedBackground
            ResolvedIsDark = $resolved.ResolvedIsDark
            PaletteApplied = $resolved.PaletteApplied
            RewriteEnabled = $resolved.RewriteEnabled
            ThinkingRewriteEnabled = $resolved.ThinkingRewriteEnabled
            InputBandRewriteEnabled = $resolved.InputBandRewriteEnabled
            LegacyColorRewriteEnabled = $resolved.LegacyColorRewriteEnabled
            BannerEnabled = $resolved.BannerEnabled
            ExitCode = $exitCode
            RawBytes = $rawInfo.Length
            PostBytes = $postInfo.Length
            RawSha256 = (Get-FileHash $rawDump -Algorithm SHA256).Hash
            PostSha256 = (Get-FileHash $postDump -Algorithm SHA256).Hash
        })
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

$summaryPath = Join-Path $OutputDirectory "summary.csv"
$results | Export-Csv -Path $summaryPath -NoTypeInformation

if (-not $SkipRender) {
    $renderer = Join-Path $toolDirectory "render.mjs"
    $nodeModules = Join-Path $toolDirectory "node_modules"
    if (-not (Test-Path $nodeModules)) {
        Write-Warning "Renderer dependencies are missing. Run 'npm ci --prefix `"$toolDirectory`"', then 'npm run render --prefix `"$toolDirectory`" -- `"$OutputDirectory`"'."
    }
    else {
        node $renderer $OutputDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "TUI renderer failed with exit code $LASTEXITCODE."
        }
    }
}

Write-Host ""
Write-Host "Capture complete: $OutputDirectory" -ForegroundColor Green
Write-Host "Summary: $summaryPath"
