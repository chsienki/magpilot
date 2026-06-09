# install-task.ps1 -- registers the MagpilotAgent scheduled task at user
# logon. Invoked by magpilot.iss at install time. Idempotent: replaces any
# existing task with the same name.
#
# Runs the installed agent EXE as the interactive user (NOT SYSTEM) so it
# can read the user's ~/.copilot/ profile (auth tokens, settings.json,
# session-state). Loads MAGPILOT_HUB_URL etc. from the installer-written
# magpilot.env file.
#
# User resolution chain (most specific first):
#   1. -User <DOMAIN\name> parameter (the installer passes this when it
#      can determine the unelevated caller's identity).
#   2. Win32_ComputerSystem.UserName -- the console-logged-in user.
#      Works when someone is actually signed in to the desktop.
#   3. quser-discovered active interactive session.
#   4. $env:USERDOMAIN\$env:USERNAME, but ONLY if it's NOT a machine
#      account (machine accounts end with '$' and Register-ScheduledTask
#      will reject them with "No mapping between account names and
#      security IDs was done").
#   5. Fail with a clear error message + exit code 2.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallDir,

    [string]$TaskName = 'MagpilotAgent',

    [string]$User,

    # V2b: optional enrollment bundle collected by the installer's
    # Pairing wizard page. When non-empty, run
    # `magpilot --magpilot-pair=<bundle>` after registering the
    # scheduled task so the agent boots fully wired up to its hub
    # on first logon. Failure to redeem the bundle doesn't abort
    # install -- the user can re-run --magpilot-pair manually.
    [string]$Bundle
)

$ErrorActionPreference = 'Stop'

$agentExe = Join-Path $InstallDir 'agent\Magpilot.Agent.exe'
$envFile  = Join-Path $InstallDir 'config\magpilot.env'

if (-not (Test-Path $agentExe)) {
    Write-Error "Agent executable not found at $agentExe"
    exit 1
}

# Fresh install: bootstrap a minimal "disconnected" magpilot.env so the
# agent boots cleanly with a unique random bearer (not the dev default,
# which would be guessable on the LAN). The user pairs the agent with a
# hub afterwards via `magpilot --magpilot-pair=<bundle>` -- that
# subcommand overwrites this file with the hub-supplied values.
#
# Upgrade: preserve the existing file verbatim. A paired agent stays
# paired across re-installs; an unpaired agent stays unpaired with its
# original token.
$envDir = Split-Path -Parent $envFile
if (-not (Test-Path $envDir)) { New-Item -ItemType Directory -Path $envDir | Out-Null }
if (-not (Test-Path $envFile)) {
    # 32 bytes of CSPRNG output -> 64-char hex. Cryptographic strength,
    # not just "random-looking".
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $bytes = New-Object byte[] 32
        $rng.GetBytes($bytes)
        $token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    } finally { $rng.Dispose() }

    $lines = @(
        '# Magpilot environment file. Initial install creates this with a',
        '# random MAGPILOT_AGENT_TOKEN so the agent has a unique bearer on',
        '# the LAN. The agent is "disconnected" until you pair it with a',
        '# hub by running:',
        '#',
        '#   magpilot --magpilot-pair=<bundle>',
        '#',
        '# Get <bundle> from the hub''s /admin/enroll page.',
        "MAGPILOT_AGENT_TOKEN=$token"
    )
    Set-Content -Path $envFile -Value $lines -Encoding ASCII
    Write-Host "Created disconnected magpilot.env at $envFile (run magpilot --magpilot-pair to wire up to a hub)."
}
else {
    Write-Host "Preserving existing magpilot.env at $envFile."
}

# v0.1.6+: the agent is a WinExe and loads magpilot.env itself, so we
# can launch the EXE directly with no powershell wrapper. The old
# wrapper (start.ps1) is now redundant and visible-windowing in two
# places: powershell.exe -WindowStyle Hidden still flashed at logon,
# AND the agent (a console app at the time) opened a second window of
# its own that stayed up for the lifetime of the session. Direct
# invocation of the WinExe avoids both.
$staleWrapper = Join-Path $InstallDir 'agent\start.ps1'
if (Test-Path $staleWrapper) {
    Remove-Item $staleWrapper -Force -ErrorAction SilentlyContinue
}

function Test-IsMachineAccount {
    param([string]$Name)
    if (-not $Name) { return $true }
    # MACHINE$ or DOMAIN\MACHINE$ both end with '$'.
    return $Name.TrimEnd().EndsWith('$')
}

function Resolve-InstallUser {
    param([string]$Explicit)

    # 1. Explicit -User wins, but only if it's a real account.
    if ($Explicit -and -not (Test-IsMachineAccount $Explicit)) {
        return $Explicit
    }

    # 2. Console-logged-in user via Win32_ComputerSystem.
    $consoleUser = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    if ($consoleUser -and -not (Test-IsMachineAccount $consoleUser)) {
        return $consoleUser
    }

    # 3. quser-discovered active interactive session. Output looks like:
    #      USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
    #     >chris                 console             1  Active      none   5/22/2026 ...
    # The first column is the username (preceded by '>' for the current
    # session); SESSIONNAME 'console' or 'rdp-tcp#N' marks an interactive
    # logon. Tolerate quser missing or returning no rows.
    try {
        $quser = & quser 2>$null
        if ($quser) {
            foreach ($line in $quser | Select-Object -Skip 1) {
                $trimmed = $line.TrimStart('>').Trim()
                $parts = $trimmed -split '\s{2,}'
                if ($parts.Count -ge 4 -and $parts[3] -eq 'Active') {
                    $name = $parts[0].Trim()
                    if ($name -and -not (Test-IsMachineAccount $name)) {
                        # quser returns the username unqualified; prepend
                        # the local machine name so Register-ScheduledTask
                        # gets a fully-qualified principal.
                        return "$env:COMPUTERNAME\$name"
                    }
                }
            }
        }
    } catch {
        # quser may be absent (Windows Home) or fail in some sessions.
        # Fall through to the env-var heuristic.
    }

    # 4. Env-var fallback (the interactive UAC-elevated install case;
    # both USERDOMAIN and USERNAME reflect the elevated user, which IS
    # the right answer).
    $fromEnv = "$env:USERDOMAIN\$env:USERNAME"
    if ($fromEnv -and -not (Test-IsMachineAccount $fromEnv)) {
        return $fromEnv
    }

    # 5. Give up with a clear message.
    return $null
}

$consoleUser = Resolve-InstallUser -Explicit $User
if (-not $consoleUser) {
    Write-Error @'
install-task.ps1: could not determine which user to register the agent task for.
None of these worked:
  - the -User parameter (not passed)
  - Win32_ComputerSystem.UserName (no console session)
  - quser (no active interactive sessions)
  - $env:USERDOMAIN\$env:USERNAME (resolved to a machine account)

Re-run with `-User "DOMAIN\name"` (e.g. `-User "SANDBOX\chris"`) to register
the agent for a specific user.
'@
    exit 2
}

Write-Host "Registering scheduled task '$TaskName' as $consoleUser"

# Tear down any prior registration so we get a clean update.
Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$action = New-ScheduledTaskAction `
    -Execute $agentExe `
    -WorkingDirectory (Split-Path -Parent $agentExe)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $consoleUser

$principal = New-ScheduledTaskPrincipal `
    -UserId $consoleUser `
    -LogonType Interactive `
    -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

Write-Host "Starting $TaskName..."
Start-ScheduledTask -TaskName $TaskName

Start-Sleep -Seconds 4

$info = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Host "  LastRunTime:    $($info.LastRunTime)"
Write-Host "  LastTaskResult: $($info.LastTaskResult)"

# V2b: optional pair-during-install. If the wizard collected an
# enrollment bundle, hand it off to the launcher`'s --magpilot-pair
# subcommand. The launcher does the heavy lifting (decode, POST
# /api/enroll/redeem, upsert magpilot.env, bounce the scheduled
# task we just started). A failed pair logs a warning but DOESN`'T
# fail the install -- the user can re-run
# `magpilot --magpilot-pair=<bundle>` from a shell to retry.
if (-not [string]::IsNullOrWhiteSpace($Bundle)) {
    $launcherExe = Join-Path $InstallDir 'bin\magpilot.exe'
    if (-not (Test-Path $launcherExe)) {
        Write-Warning "Launcher exe not found at $launcherExe; can't auto-pair. Run magpilot --magpilot-pair=<bundle> manually."
    }
    else {
        Write-Host "Pairing with hub..."
        # Wait for the agent to bind its port before pairing -- the
        # pair flow bounces the scheduled task we just started, and
        # an immediate restart inside the 4-second StartTask window
        # confuses Start-ScheduledTask`'s "is it running" check. The
        # sleep above is usually enough; add a tiny extra cushion.
        Start-Sleep -Seconds 2

        $pairResult = & $launcherExe "--magpilot-pair=$Bundle" 2>&1
        $pairExit = $LASTEXITCODE
        $pairResult | ForEach-Object { Write-Host "  pair: $_" }
        if ($pairExit -ne 0) {
            Write-Warning "magpilot --magpilot-pair exited with code $pairExit. The agent is installed but unpaired. Re-run from a shell to retry."
        }
        else {
            Write-Host "  pair: completed successfully."
        }
    }
}
else {
    Write-Host "No -Bundle supplied; agent installed in disconnected mode. Run magpilot --magpilot-pair=<bundle> from /admin/enroll when ready."
}

