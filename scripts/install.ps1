#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Bootstrap magpilot on Windows -- download, verify, and run the latest
    installer.

.DESCRIPTION
    Downloads the latest magpilot installer from GitHub Releases, verifies
    its SHA256 against the matching .sha256 asset, and runs it (silently
    or interactively). Intended for the "one-liner" install path:

        irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1 | iex

    For parameter passing (e.g. -Silent, a pinned -Version, or -DryRun),
    fetch and invoke as a script block:

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1))) -Silent

    Once installed, `magpilot --magpilot-update` handles future upgrades
    via the same SHA256-verified path.

.PARAMETER Repo
    GitHub owner/repo to fetch from. Defaults to chsienki/magpilot.
    Override when running a fork or a mirror.

.PARAMETER Version
    Specific semver to install (e.g. "0.1.8"). Omit to use the latest
    published release.

.PARAMETER Silent
    Pass /SILENT to the installer so the wizard runs unattended. The
    Settings page values (hub URL, agent token, public URL) still need
    to come from somewhere -- either the existing config file from a
    prior install, or by editing %ProgramFiles%\Magpilot\config\magpilot.env
    afterwards.

.PARAMETER DryRun
    Download and verify, but don't run the installer. Useful for CI /
    debugging.
#>
[CmdletBinding()]
param(
    [string]$Repo = "chsienki/magpilot",
    [string]$Version,
    [switch]$Silent,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.Platform -and $PSVersionTable.Platform -ne "Win32NT") {
    throw "magpilot installer is Windows-only. Detected platform: $($PSVersionTable.Platform)"
}

# 1. Resolve the target version. The version.json asset on the latest
#    release carries the canonical semver, the protocol range, and is
#    cheap to fetch via the /releases/latest/download/ redirect endpoint
#    (no GitHub API token needed).
if ([string]::IsNullOrEmpty($Version)) {
    $versionUrl = "https://github.com/$Repo/releases/latest/download/version.json"
    Write-Host "Fetching latest release metadata from $versionUrl ..."
    try {
        $meta = Invoke-RestMethod -Uri $versionUrl -UseBasicParsing
        $Version = [string]$meta.version
    }
    catch {
        throw "Could not fetch latest release metadata. Is $Repo's most-recent release published (not draft)? Underlying error: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrEmpty($Version)) {
        throw "version.json from $Repo did not include a 'version' field."
    }
}
Write-Host "Target version: $Version"

# 2. Build the asset URLs. The tag layout is `v<semver>`.
$exeName = "magpilot-setup-$Version.exe"
$exeUrl  = "https://github.com/$Repo/releases/download/v$Version/$exeName"
$shaUrl  = "$exeUrl.sha256"

# 3. Download to a unique temp dir so concurrent installs / leftovers
#    don't collide. Preserved on success so a failed install can be
#    re-run from the same artifacts.
$tmpRoot = Join-Path $env:TEMP ("magpilot-install-{0}" -f (Get-Random))
New-Item -ItemType Directory -Path $tmpRoot | Out-Null
$exePath = Join-Path $tmpRoot $exeName
$shaPath = "$exePath.sha256"

Write-Host "Downloading installer: $exeUrl"
Invoke-WebRequest -Uri $exeUrl -OutFile $exePath -UseBasicParsing

Write-Host "Downloading checksum:  $shaUrl"
Invoke-WebRequest -Uri $shaUrl -OutFile $shaPath -UseBasicParsing

# 4. Verify SHA256. The .sha256 file is the standard `<hash>  <name>`
#    format produced by `Get-FileHash` + a tab/space + filename.
$expectedRaw = Get-Content -Raw $shaPath
$expected = ($expectedRaw -split '\s+', 2)[0].ToLowerInvariant()
if ($expected.Length -ne 64) {
    throw "Checksum file at $shaUrl did not parse to a 64-char hex hash: '$expected'"
}
$actual = (Get-FileHash -Algorithm SHA256 -Path $exePath).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    throw "SHA256 mismatch -- the download is corrupt or tampered with. Expected $expected, got $actual. Aborting."
}
Write-Host "SHA256 OK: $actual"

if ($DryRun) {
    Write-Host "Dry-run -- installer NOT executed. Artifacts at: $tmpRoot"
    return
}

# 5. Run the installer. Inno Setup respects /SILENT for unattended
#    installs; without it the wizard opens normally (Settings page
#    pre-populated from any existing magpilot.env on upgrade).
#    Start-Process -Verb RunAs gets us UAC elevation on Windows.
$installerArgs = @()
if ($Silent) { $installerArgs += "/SILENT" }

Write-Host "Running installer (may prompt for elevation)..."
$start = @{
    FilePath = $exePath
    Verb     = "RunAs"
    Wait     = $true
}
if ($installerArgs.Count -gt 0) {
    $start.ArgumentList = $installerArgs
}
$proc = Start-Process @start -PassThru
if ($proc.ExitCode -ne 0) {
    throw "Installer exited with code $($proc.ExitCode). Artifacts preserved at $tmpRoot for retry."
}
Write-Host "Install complete. Artifacts preserved at: $tmpRoot"
Write-Host "Run 'magpilot --magpilot-version' from a new shell to confirm."
