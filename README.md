# MagPilot

<p align="center">
  <img src="assets/magpilot-logo.png" alt="MagPilot mascot: a magpie pilot perched on a vintage CRT terminal" width="300" />
</p>

> Bring the GitHub Copilot CLI experience to your phone.

`magpilot` is a small system that lets you drive `copilot` (the GitHub
Copilot CLI) from your phone, against any of your computers, over your
home VPN.

It is **not** another agent platform. It does not have plugins, cron
jobs, web dashboards, or a smart-home integration. (For that, see
[openclaw](https://github.com/openclaw/openclaw).) MagPilot's only
goal is "I want a chat client on my phone that talks to Copilot CLI
running on one of my real computers."

---

## Why

Chris's setup:

- A workstation (HENDRIK), a Mac, and a few other machines, each with
  GitHub Copilot CLI installed and authenticated.
- A WireGuard VPN that lets the phone reach the home LAN from anywhere.
- A docker LXC at `192.168.1.239` that already runs always-on services.

What he wants from his phone:

1. A chat UI that connects to a real `copilot` process on a chosen
   host, with the **full agent capability** — tool calls, file edits,
   shell commands, MCP servers.
2. The ability to **see all sessions across all machines**, including
   ones currently running in a terminal somewhere.
3. The ability to **take over** an in-progress terminal session
   (kill it, resume under our control) so a chat started at the desk
   can be continued from the couch.
4. Approval prompts (for risky tools) that show up as proper modals
   on the phone, not blanket allow-all.
5. Push notifications when an agent finishes a long-running task or
   needs an answer while the app is backgrounded.

## How (one paragraph)

A small **per-host agent** daemon runs on each computer. It speaks
the [Agent Client Protocol (ACP)](https://agentclientprotocol.com/)
to a single `copilot --acp --port <N>` child, which gives us
JSON-RPC access to the full Copilot CLI agent — first-class
sessions, structured streaming, real per-tool approvals. The agent
exposes a tiny HTTP+SSE API to the LAN.

A central **hub** daemon runs on the docker LXC. It auto-discovers
agents via UDP broadcast, aggregates their sessions, proxies
streams, and is the single endpoint clients talk to. This makes
the WireGuard story simple: one address (`https://magpilot.home.sienkiewi.cz`)
forever, hub deals with finding and routing to the actual hosts.

Two clients consume the hub's API and share **a single Blazor UI
codebase** (MAUI Blazor Hybrid):

- A **.NET MAUI Android app** on the Pixel that hosts the Blazor UI
  in a WebView. Bearer-token auth, FCM push.
- A **Blazor WebAssembly SPA** at `https://magpilot.home.sienkiewi.cz`
  for any browser. GitHub OAuth login (allowlisted to a single user),
  Web Push notifications.

## Components

| Name              | What                                                     | Where it runs              |
|-------------------|----------------------------------------------------------|----------------------------|
| `Magpilot.Agent`  | ACP-to-HTTP/SSE adapter, one per machine                 | HENDRIK, Linux container, etc. |
| `Magpilot.Hub`    | Aggregator, discovery, OAuth, central log, serves the web SPA | docker LXC (CT 102)   |
| `Magpilot.UI`     | Shared Blazor UI library (chat, sessions, theme)         | Future MAUI WebView + browser |
| `Magpilot.Web`    | Blazor WASM shell for browsers                           | Browser, served by hub     |
| `Magpilot.Shared` | DTOs + SSE wire types (the contract between agent + UI)  | n/a (referenced)           |

## Status

**Live since 2026-04** at `https://magpilot.home.sienkiewi.cz`. The hub
runs on a docker LXC (CT 102), agents on each host (HENDRIK + a Linux
container called `magnus`). Day-to-day usage covers chatting from any
browser, hopping between Owned / Locked / Dormant sessions, full ACP
tool-call streaming, central log viewer at `/admin/logs`. As of
2026-05-14, **GitHub OAuth** is wired (allowlisted username); the
**`magpilot` launcher** ships a coordinated `copilot` shim that
preempts cooperatively with the SPA + WhatsApp via a single-owner
handoff (no events.jsonl forks). The **Windows installer** + autoupdate
path (`installer/magpilot.iss`, `magpilot --magpilot-update`,
hub-mediated version check) is in place; HENDRIK runs the agent as a
scheduled task at user logon.

What is **NOT yet wired**: the MAUI Android shell (the original phone
target), real FCM/Web Push delivery, TLS for hub<->agents (still LAN +
bearer), and approval-prompt modals for risky tool calls. See
`docs/plan.md` for the full roadmap.

## Repository layout

```
magpilot/
   Magpilot.slnx
   docs/plan.md              <- design doc (start here for the long version)
   docs/architecture.md      <- topology + the agent HTTP contract
   .github/copilot-instructions.md  <- orientation for AI agents working on this repo
   spikes/acp-smoke/         <- standalone ACP smoke test
   scripts/build-hub.ps1     <- builds web SPA + copies into hub wwwroot
   scripts/test-shim-phase1.sh <- bash acceptance test for the shim endpoints
   src/
      Magpilot.Shared/      <- DTOs, SSE event types (incl. shim contract)
      Magpilot.Agent/       <- per-host daemon (ACP wrapper + HTTP/SSE API
                                + HostOwnership for the cooperative handoff)
      Magpilot.Hub/         <- central daemon (proxy, OAuth, SPA host,
                                central /api/log sink + viewer)
      Magpilot.Host/        <- the `magpilot` launcher (assembly: magpilot.exe).
                                Cooperative coord with the agent so a session
                                is driven by exactly one process at a time.
      Magpilot.UI/          <- shared Blazor components (chat, sessions,
                                MagpilotTheme, MagpieMark, ChatView,
                                HubClient, HubLogClient, JsErrorBridge)
      Magpilot.Web/         <- Blazor WASM shell for the browser
   deploy/                   <- docker-compose + ship-image notes for the hub
```

## Build & run locally

```pwsh
# Build everything
dotnet build

# Run the agent (in one terminal)
$env:MAGPILOT_AGENT_TOKEN = "dev-token"
$env:ASPNETCORE_URLS       = "http://localhost:5099"
dotnet run --project src/Magpilot.Agent

# Build the SPA + copy it into the hub's wwwroot
./scripts/build-hub.ps1

# Run the hub (in another terminal)
$env:MAGPILOT_HUB_BEARER  = "dev-bearer"
$env:MAGPILOT_AGENT_TOKEN = "dev-token"
$env:MAGPILOT_DEV_BYPASS_AUTH = "true"
$env:ASPNETCORE_URLS       = "http://localhost:7088"
dotnet run --project src/Magpilot.Hub

# Open http://localhost:7088/  (web SPA; OAuth bypassed in dev mode)
# Or curl with bearer:
#   curl -H "Authorization: Bearer dev-bearer" http://localhost:7088/api/agents
```

See `deploy/README.md` for the LXC docker recipe.

## Install (Windows agent + launcher)

Two steps: install, then click Adopt in the hub. **No bundles, no
copy/paste, no env vars.**

**1. Install** (admin not required upfront -- the installer asks for
elevation when it needs it):

```pwsh
irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1 | iex
```

That downloads the latest signed installer from
[Releases](https://github.com/chsienki/magpilot/releases), verifies its
SHA256 against the matching `.sha256` asset, and runs it. The wizard
just collects target directory + scheduled-task settings -- no
secrets, no hub URL. After the files copy and the scheduled task
registers, the installer kicks off interactive pairing:

* The launcher (`magpilot --magpilot-pair`) broadcasts a UDP query
  on the LAN. Any magpilot hub replies with its URL.
* If one hub is found: auto-pick. Multiple hubs: a console picker.
* The launcher submits a pairing claim to the chosen hub and opens
  your browser to `https://<hub>/admin/agents?pending=<id>`.
* It prints a 6-character fingerprint on the console and the same
  fingerprint shows next to the pending request in the hub's SPA.
  Visually compare them, then click **Adopt**.
* The launcher's long-poll picks up the approval, the hub mints a
  fresh per-agent token, the launcher writes `magpilot.env`, the
  scheduled task is bounced. Done.

For unattended installs (no UDP discovery available, scripted
deployment), pair from a shell with a bundle minted on the hub's
`/admin/enroll` page:

```pwsh
magpilot --magpilot-pair=<bundle>
```

For silent unattended install:

```pwsh
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1))) -Silent
```

After install + pair, future upgrades go through `magpilot --magpilot-update`
(same SHA256-verified path; preserves the existing config so re-pairing
isn't needed). Re-run `magpilot --magpilot-pair` to re-point an existing
agent at a different hub or rotate its credentials.

**Revoking a paired agent.** Open `https://<your-hub>/admin/agents`,
find the agent, click **Revoke**. Hub-to-agent calls return 410 Gone
with a "re-pair" hint. Reversible: re-run `magpilot --magpilot-pair`
on the agent machine and it's back in service.

## Architectural law

> **Satellites know about magpilot. Magpilot does NOT know about satellites.**

If you want magpilot to do something for an external service, the right
answer is almost always a deployment-time bootstrap hook (see
`MAGPILOT_BOOTSTRAP_HOOK_DIR` in `src/Magpilot.Agent/bootstrap.sh`) or a
new HTTP-API consumer, not a magpilot patch. Don't add agent-specific
code paths or hostnames into magpilot itself.

## Related context

- An example **outer-ring deployment** that consumes magpilot as a git
  submodule + adds a personal-assistant product, a WhatsApp bridge, a
  cron sidecar, and a context-loader on top:
  [`chsienki/magstronaut`](https://github.com/chsienki/magstronaut)
  (private; reach out if you'd like to see how it's wired).
- The home-network and openclaw task-context docs for Chris's deployment
  live in a separate repo at `chsienki/copilot-context`.
