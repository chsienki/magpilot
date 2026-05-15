# firewall.ps1 -- adds or removes inbound Windows Firewall rules for the
# Magpilot agent: TCP 5099 (HTTP+SSE API) and UDP 47823 (UDP discovery).
# Restricted to RFC1918 + WireGuard CIDRs by default; broaden via the
# -RemoteAddress parameter if you need other reach.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Add', 'Remove')]
    [string]$Action,

    # Default to private network ranges + the magpilot WireGuard subnet.
    [string[]]$RemoteAddress = @('192.168.0.0/16', '10.0.0.0/8', '172.16.0.0/12')
)

$ErrorActionPreference = 'Stop'

$tcpName = 'Magpilot Agent (TCP 5099)'
$udpName = 'Magpilot Agent Discovery (UDP 47823)'

if ($Action -eq 'Remove') {
    Write-Host "Removing firewall rules..."
    Remove-NetFirewallRule -DisplayName $tcpName -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName $udpName -ErrorAction SilentlyContinue
    return
}

# Action == Add. Replace any prior rule of the same name (idempotent install).
Get-NetFirewallRule -DisplayName $tcpName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName $udpName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

Write-Host "Adding firewall rule: $tcpName ($RemoteAddress)"
New-NetFirewallRule -DisplayName $tcpName `
    -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow `
    -RemoteAddress $RemoteAddress -Profile Any | Out-Null

Write-Host "Adding firewall rule: $udpName ($RemoteAddress)"
New-NetFirewallRule -DisplayName $udpName `
    -Direction Inbound -Protocol UDP -LocalPort 47823 -Action Allow `
    -RemoteAddress $RemoteAddress -Profile Any | Out-Null

Write-Host "Done."
