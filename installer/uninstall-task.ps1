# uninstall-task.ps1 -- stops + removes the MagpilotAgent scheduled task.
# Idempotent: tolerates the task not existing.

[CmdletBinding()]
param(
    [string]$TaskName = 'MagpilotAgent'
)

$ErrorActionPreference = 'SilentlyContinue'

$task = Get-ScheduledTask -TaskName $TaskName
if ($null -ne $task) {
    Write-Host "Stopping + unregistering $TaskName..."
    Stop-ScheduledTask -TaskName $TaskName
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Best-effort: kill any lingering Magpilot.Agent.exe (e.g. one started by
# the user manually outside the scheduled task).
Get-Process -Name 'Magpilot.Agent' | ForEach-Object {
    Write-Host "Stopping running Magpilot.Agent PID $($_.Id)"
    Stop-Process -Id $_.Id -Force
}
