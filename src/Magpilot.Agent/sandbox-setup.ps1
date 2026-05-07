$ErrorActionPreference = "Stop"

Write-Host "=== Firewall rule for TCP 5099 ==="
$existing = Get-NetFirewallRule -DisplayName "Magpilot Agent (5099)" -EA SilentlyContinue
if ($existing) { Remove-NetFirewallRule -DisplayName "Magpilot Agent (5099)" }
New-NetFirewallRule -DisplayName "Magpilot Agent (5099)" `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5099 `
    -Profile Any | Select-Object DisplayName, Profile, Enabled | Format-List

Write-Host "=== Scheduled task at startup ==="
$existingTask = Get-ScheduledTask -TaskName MagpilotAgent -EA SilentlyContinue
if ($existingTask) { Unregister-ScheduledTask -TaskName MagpilotAgent -Confirm:$false }

$action = New-ScheduledTaskAction `
    -Execute "C:\Program Files\PowerShell\7\pwsh.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -File C:\tools\Magpilot.Agent\start.ps1"
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName MagpilotAgent `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Write-Host "  registered"

Write-Host "=== Start it now ==="
Start-ScheduledTask -TaskName MagpilotAgent
Start-Sleep -Seconds 18

Write-Host "=== Status ==="
Get-ScheduledTaskInfo -TaskName MagpilotAgent | Select-Object LastRunTime, LastTaskResult | Format-List
$proc = Get-Process Magpilot.Agent -EA SilentlyContinue
if ($proc) { Write-Host "  Magpilot.Agent running, pid $($proc.Id)" }
else { Write-Host "  process not running yet" }

Write-Host "=== healthz ==="
try {
    $r = Invoke-WebRequest -Uri http://localhost:5099/healthz -UseBasicParsing -TimeoutSec 5
    Write-Host "  HTTP $($r.StatusCode)"
} catch {
    Write-Host "  failed: $($_.Exception.Message)"
}
