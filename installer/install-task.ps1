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

    [string]$User
)

$ErrorActionPreference = 'Stop'

$agentExe = Join-Path $InstallDir 'agent\Magpilot.Agent.exe'
$envFile  = Join-Path $InstallDir 'config\magpilot.env'

if (-not (Test-Path $agentExe)) {
    Write-Error "Agent executable not found at $agentExe"
    exit 1
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

