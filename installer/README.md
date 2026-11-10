# Windows agent and launcher

The Windows x64 installer adds a per-host Magpilot agent and the `magpilot`
terminal command. The agent runs as an interactive user so it can access that
user's Copilot authentication, projects, and session store.

You need a reachable [hub](../deploy/README.md), GitHub Copilot access, and an
installed, authenticated [Copilot CLI](https://github.com/github/copilot-cli)
for terminal use. The agent's SDK runtime is bundled separately.

## Install

Run in PowerShell:

```pwsh
irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1 | iex
```

Or download `magpilot-setup-<version>.exe` from
[Releases](https://github.com/chsienki/magpilot/releases).
The script downloads the installer and matching `.sha256` asset, verifies the
checksum, then requests UAC elevation. The installer is **unsigned**;
checksum verification is not an Authenticode signature, and Windows may show
a SmartScreen warning.

Select the agent and launcher components and the desired PATH, scheduled-task,
and firewall options. The wizard asks for no hub URL or tokens.

With the agent's logon task selected, setup:

1. Preserves an existing `magpilot.env`, or creates a disconnected one with a
   cryptographically random agent bearer.
2. Registers `MagpilotAgent` at user logon, running as that user rather than
   `SYSTEM`, and starts the agent executable directly.
3. Starts interactive pairing. Cancellation or pairing failure leaves the
   installed files and existing settings intact.

Open a new terminal for the updated `PATH`.

### Installed layout

```text
%ProgramFiles%\Magpilot\
  agent\Magpilot.Agent.exe       Self-contained .NET 9 service
  agent\...                     Bundled SDK runtime and dependencies
  bin\magpilot.exe              Native AOT .NET 10 launcher
  bin\conpty.dll                App-local PTY support
  bin\x64\OpenConsole.exe
  bin\arm64\OpenConsole.exe
  config\magpilot.env            Pairing and local settings
  install-task.ps1
  uninstall-task.ps1
  firewall.ps1
```

The scheduled task launches `Magpilot.Agent.exe` directly; there is no
generated `start.ps1` wrapper. The installer does not replace `copilot` on
`PATH`. Copilot sessions remain under the user's `.copilot/session-state`,
not under Program Files.

## Pairing

### Interactive discovery

Setup starts this flow; repeat it with:

```pwsh
magpilot --magpilot-pair
```

The launcher broadcasts on UDP 47824, chooses the only responding hub or asks
you to select one, and opens its **Agents** page. Compare the six-character
fingerprint in the console with the pending request before clicking **Adopt**.
Claims expire after five minutes.

Approval writes `MAGPILOT_HUB_URL` and a minted `MAGPILOT_AGENT_TOKEN` into
`config\magpilot.env`, preserving unrelated settings, then restarts the
installed task. This flow **does not receive a hub bearer**. Session access
works without one, but agent log forwarding and release polling need
`MAGPILOT_HUB_BEARER`; provision it separately or use the bundle flow below.

Only allow mutually trusted hub users. Pending claims are visible to all
authenticated users and are not restricted to the administrator or an intended
owner. See the [authentication model](../docs/architecture.md#trust-boundaries).

### Enrollment bundle

Use `/admin/enroll` on the hub to create a bundle. It expires after 15 minutes
and its voucher is single-use:

```pwsh
magpilot "--magpilot-pair=<bundle>"
```

This bypasses UDP discovery and the local browser. Redemption writes the hub
URL, fresh agent token, and the bundle's hub bearer. The signed-in user who
issued the voucher becomes the agent owner. Keep the bundle secret: its
infrastructure bearer can reach every agent through the hub.

Pairing does not itself solve hub-to-agent routing. A host whose address cannot
be discovered still needs a reachable URL in the hub registry; see
[cross-network discovery](../deploy/README.md#discovery-across-networks).

### Custom settings files and provisioning

`MAGPILOT_ENV_FILE` overrides the native agent's settings load path and the
launcher's **pairing output** path. When it is set, pairing writes that file
without restarting the installed task, so development and provisioning cannot
bounce an unrelated agent.

Normal launcher connection/theme lookup still uses process environment
variables or the settings file relative to its executable; it does not read
the pairing-output override. Export the appropriate `MAGPILOT_AGENT_URL` and
`MAGPILOT_AGENT_TOKEN` when using a launcher against a custom source agent.

Pairing defaults to the current hostname; `MAGPILOT_AGENT_NAME` can choose the
name being enrolled. It does not rename an agent's discovery identity. For
containers, enroll the actual container hostname.

Settings-file writes preserve comments and unrelated keys. Restart a running
agent after changing settings. Native agent environment variables already set
in its process override values loaded from the file.

## Updates and revocation

```pwsh
magpilot --magpilot-version
magpilot --magpilot-update
```

The updater uses release metadata cached by the local agent, downloads the
matching installer and checksum, then runs setup with `/SILENT`. The hub's
**Check for agent updates** action refreshes metadata; it never installs on
remote machines. See [update discovery](../deploy/README.md#windows-update-discovery).

Existing configuration survives setup. The task helper also attempts
interactive pairing during an upgrade; cancelling that attempt does not erase
the preserved configuration. Close other terminal sessions before updating
when possible: mapped executables can leave a partial upgrade.

The agent and launcher are separate binaries. Check both when an update looks
incomplete:

```pwsh
Invoke-RestMethod http://127.0.0.1:5099/api/version
(Get-Item 'C:\Program Files\Magpilot\bin\magpilot.exe').VersionInfo.ProductVersion
```

Release availability is compared with the launcher's version, not just the
agent's, so an older launcher can still offer an update beside a newer agent.
Close remaining launcher sessions and rerun setup if necessary; copying a
managed `magpilot.dll` beside the Native AOT executable does not update it.

To revoke a host, use **Revoke** on the hub's **Agents** page. Subsequent
proxied requests return `410 Gone`. Re-pairing with fresh credentials restores
access.

## Service and firewall operations

```pwsh
Get-ScheduledTaskInfo -TaskName MagpilotAgent
Stop-ScheduledTask -TaskName MagpilotAgent
Start-ScheduledTask -TaskName MagpilotAgent
```

The default inbound firewall rules allow TCP 5099 and UDP 47823 from RFC1918
private ranges. Other VPN source ranges require an explicit policy:

```pwsh
& 'C:\Program Files\Magpilot\firewall.ps1' -Action Add `
    -RemoteAddress '192.168.0.0/16','10.0.0.0/8','172.16.0.0/12'
```

Do not expose the plaintext agent API to the public internet.

## Scripted and headless installation

Bootstrap parameters are `-Repo`, `-Version`, `-Silent`, and `-DryRun`:

```pwsh
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1))) -Silent
```

`-DryRun` downloads and verifies without executing. `-Version` selects a
published semver; `-Repo` selects a fork or mirror. Anonymous downloads require
a public repository and published release assets, not a draft.

`-Silent` suppresses the installer wizard; it does not eliminate UAC or make
interactive pairing headless. Finish unattended provisioning with the bundle
flow in the target user's context.

If setup runs from SSH or `SYSTEM`, explicitly register the intended logon
user rather than a machine account:

```pwsh
& 'C:\Program Files\Magpilot\install-task.ps1' `
    -InstallDir 'C:\Program Files\Magpilot' -User 'DOMAIN\user'
```

The helper tries its explicit user, console user, active `quser` session, then
environment identity, rejecting machine accounts. It exits with code 2 if none
resolves. Inspect an Inno Setup `/LOG=<path>` log for helper exit codes.

## Build the installer

From the repository root, use Windows x64, the .NET 10 SDK, the Native AOT
C++/Windows SDK toolchain, and [Inno Setup 6](https://jrsoftware.org/isinfo.php).
The release workflow also builds and tests the solution before packaging.

```pwsh
$version = (Get-Content VERSION).Trim()
dotnet publish .\src\Magpilot.Agent -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=false -o .\publish\agent
dotnet publish .\src\Magpilot.Host -c Release -r win-x64 -o .\publish\bin
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' `
    "/DAppVersion=$version" "/DPublishDir=$PWD\publish" .\installer\magpilot.iss
Get-Item ".\installer\installer-output\magpilot-setup-$version.exe"
```

Keep the launcher's app-local `conpty.dll` and architecture-specific
`OpenConsole.exe` files beside the published executable. The presence of both
OpenConsole architectures does not make the x64 launcher an ARM64 package.
