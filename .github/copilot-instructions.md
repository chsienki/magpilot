# Magpilot — Copilot agent instructions

> Read this first. It captures the architecture, the moving parts, the
> non-obvious gotchas, and the build/deploy workflow you need to make
> changes confidently. The README is for humans; this file is for you.

## Architectural law

> **Satellites know about magpilot. Magpilot does NOT know about satellites.**

Magpilot is a generic platform: a multi-host Copilot CLI multiplexer +
SPA. External products (assistants, chat-bridges, schedulers,
launchers) live in their own repos and consume magpilot's HTTP API.
None of them is mentioned by name in magpilot code.

If you find yourself wanting to add an `if (agent == "magnus")` branch,
a `WhatsApp`-named class, a deployment-specific `bootstrap.sh` block, or
a hard-coded sessionId, **stop**: the right answer is almost always
either a deployment-time **bootstrap hook** (see
`MAGPILOT_BOOTSTRAP_HOOK_DIR` in `src/Magpilot.Agent/bootstrap.sh`) or a
new HTTP-API consumer in the deployer's own repo.

## What this repo is

Magpilot puts the GitHub Copilot CLI on the user's phone (and any
browser) by:

1. Running `copilot --acp` (Agent Client Protocol over JSON-RPC on
   stdio) on each real machine, wrapped by a small **per-host agent**
   daemon that exposes an HTTP+SSE API to the LAN.
2. A central **hub** daemon on the docker LXC (102, `192.168.1.239`)
   that auto-discovers agents over UDP broadcast, aggregates their
   sessions, proxies streams, and serves the web SPA + handles auth.
3. Clients (a Blazor WebAssembly SPA today; a MAUI Blazor Hybrid
   Android shell later) consume the hub's API.

The user-facing URL is `https://magpilot.home.sienkiewi.cz`
(LAN/WireGuard only, fronted by NPM on the same LXC).

## Project layout

```
src/
  Magpilot.Shared/   <- DTOs, SSE event types (StreamEvent discriminator).
                       Includes the shim contract: SessionStateInfo,
                       SessionOwner, SessionActivity, InFlightInfo,
                       LastEventInfo, AcquireForHostBody,
                       ReleaseRequestBody, ReleaseFromHostBody,
                       HostOwnedResponse, ReleaseRequested SSE case.
  Magpilot.Agent/    <- per-host daemon: ACP client + minimal HTTP/SSE API
    Acp/AcpSessionManager.cs       <- the heart; ACP <-> SSE translation.
                                      Has _inFlight tracking +
                                      WaitForTurnBoundaryAsync used by
                                      AcquireForHostAsync.
    Acp/AcpClient.cs               <- one process. Resolves exe full path
                                      (Process.Start launcher-shim fix),
                                      reads settings.json and forwards
                                      --plugin-dir per enabled plugin.
                                      Exposes IsAlive + FailAllPending
                                      so AcpFlavorPool can detect dead
                                      children and fail in-flight calls fast.
    Acp/AcpFlavorPool.cs           <- one client per AcpFlavor key.
                                      AcquireAsync respawns dead cached
                                      multiplex clients (see "Dead-child
                                      respawn" gotcha below).
    Api/AgentEndpoints.cs          <- HTTP endpoints (sessions, /messages,
                                      /history, /quick-prompt, /stream SSE).
                                      Plus the shim Phase 1+ endpoints:
                                      GET /state, POST /release-request,
                                      POST /acquire-for-host, POST /release.
                                      /messages, /interrupt, /approvals
                                      return 409 when host-owned.
    Sessions/SessionScanner.cs     <- discovers Owned/Locked/Dormant sessions
    Sessions/SessionRegistry.cs    <- composes scanner + ACP + HostOwnership.
                                      GetState / AcquireForHostAsync /
                                      ReleaseFromHostAsync.
    Sessions/HostOwnership.cs      <- AUTHORITATIVE in-memory map of
                                      sessionId -> hostPid for sessions
                                      a magpilot launcher holds. The
                                      filesystem inuse.lock files are
                                      advisory (see "lock files are NOT
                                      a mutex" gotcha below).
    Sessions/HistoryReader.cs      <- reads events.jsonl directly so the
                                      SPA can rehydrate Owned-without-cache
    Logging/HubLoggerProvider.cs   <- mirrors Warning+ to hub /api/log/batch
  Magpilot.Hub/      <- central daemon: agent registry, proxy, SPA host, auth
    Agents/AgentHttpClient.cs      <- ClientFor(name, kind): three named HttpClients
                                      (Read 10s / Action 90s / Stream infinite).
                                      See "Hub -> agent HTTP timeouts" gotcha.
    Logging/LogStore.cs            <- SQLite + FTS5 central log; trim loop
    Logging/LogModels.cs           <- ingest + query DTOs
    Api/LogEndpoints.cs            <- POST /api/log[/batch], GET /api/log[/sources]
    Api/HubEndpoints.cs            <- per-agent proxy. Routes pick AgentClientKind
                                      per call. Forwards the four shim routes
                                      (/state, /release-request, /acquire-for-host,
                                      /release) verbatim via Forward(resp).
    Auth/HubAuth.cs                <- cookie auth + GitHub OAuth (with
                                      ReturnUrl bounce for satellite SPAs).
  Magpilot.Host/     <- the `magpilot` launcher (assembly: magpilot.exe).
                       PATH-installed as `magpilot` (NOT as `copilot` -- it
                       does not shadow the real binary). Coordinates with
                       the agent so a session is driven by exactly one
                       process at a time. See the "Cooperative single-
                       owner handoff" section below.
    WrapperOptions.cs              <- --magpilot-* flag parser
    AgentClient.cs                 <- HTTP + SSE wrapper for the shim endpoints
    CopilotLocator.cs              <- finds the real copilot binary while
                                      avoiding the wrapper itself
    PtyHost.cs                     <- spawns copilot in a real PTY via
                                      sch.pty.net; bidirectional pump;
                                      ShutdownGracefullyAsync writes /exit\r
                                      to the PTY master before falling back
                                      to PTY.Kill.
    RawConsoleMode.cs              <- SetConsoleMode (Win) + tcsetattr (Unix)
                                      raw-mode toggle around the PTY session.
    Program.cs                     <- main orchestration loop.
  Magpilot.UI/       <- shared Blazor components (RCL, MudBlazor 9.4)
    MagpilotTheme.cs               <- light + dark MudThemes (deep midnight blue)
    ThemeState.cs                  <- cascading dark-mode toggle
    Components/MagpieMark.razor    <- single source of truth for the bird
                                      mark (params: Size, Glow, Class).
                                      Asset served via RCL at
                                      _content/Magpilot.UI/favicon-mark.svg.
    Components/HostName.razor      <- single source of truth for "host name +
                                      status indicator". AppBar variant is an
                                      outlined transparent pill that tracks
                                      the live SSE stream state (Connected /
                                      Reconnecting / Offline) via Color +
                                      Indeterminate; drawer-header variant is
                                      plain inline.
    Pages/Home.razor               <- main chat (per-session cache, SSE consumer,
                                      three-way Owned routing, auto-reconnect on
                                      backgrounded-tab disconnects -- see SPA section).
                                      HandleSend catches HostStillOwnedException
                                      and surfaces a "Take over from terminal"
                                      MudAlert; Apply() handles ReleaseRequested
                                      to surface the "host has taken over" alert
                                      with HandleTakeBackFromHost wired to its
                                      "Take back" button (see "Cooperative handoff").
    Pages/Logs.razor               <- /admin/logs viewer. Routes through
                                      HubClient -- NOT bare HttpClient
                                      (see SPA pitfalls).
    Components/ChatView.razor      <- renders messages (assistant=markdown, thought=details)
    Components/MarkdownView.razor  <- Markdig wrapper
    Services/HubClient.cs          <- per-session/agent API calls. SendPromptAsync
                                      retries-on-409 (release-request + poll);
                                      ForceTakeOverAndSendAsync drives the
                                      "Take over from terminal" path.
                                      HostStillOwnedException is the typed
                                      surface for the polite-knock timeout.
                                      StreamAsync silently yield-breaks on
                                      browser-side network drops so Home.razor's
                                      pump can transparently reconnect.
    Services/HubLogClient.cs       <- bounded queue + drain; posts /api/log
    Services/JsErrorBridge.cs      <- static [JSInvokable] for window.onerror
  Magpilot.Web/      <- Blazor WASM shell (built into Magpilot.Hub/wwwroot/)
    wwwroot/js/error-capture.js    <- window.onerror + unhandledrejection
deploy/                <- docker-compose + deploy notes for LXC 102
deploy/magnus/         <- example always-on agent deployment recipe
docs/plan.md           <- full design doc (v5: Blazor Hybrid + Web). Read for context.
docs/architecture.md   <- topology + the agent HTTP contract. Read before
                          touching SSE / quick-prompt / pinned sessions.
spikes/acp-smoke/      <- standalone ACP smoke test (Node.js)
scripts/build-hub.ps1  <- publishes Web SPA -> copies into Hub/wwwroot
scripts/test-shim-phase1.sh <- bash acceptance test for the four shim endpoints.
                               Honors TEST_HOST_PID env override (MSYS2 bash's $$
                               is an internal PID the Win32 process table can't see).
```

`Magpilot.slnx` (XML solution format) is the solution. There is no
`.sln`. `dotnet build` / `dotnet test` understand `.slnx`.

## Build, run, deploy

```pwsh
# Build everything
dotnet build

# Run the agent (terminal 1; on the host whose Copilot CLI you want to drive)
$env:MAGPILOT_AGENT_TOKEN      = "dev-token"
$env:MAGPILOT_AGENT_PUBLIC_URL = "http://localhost:5099"   # what the hub uses to reach back
$env:ASPNETCORE_URLS            = "http://localhost:5099"
dotnet run --project src/Magpilot.Agent

# Build SPA + copy into hub wwwroot, then run hub (terminal 2)
./scripts/build-hub.ps1
$env:MAGPILOT_HUB_BEARER       = "dev-bearer"
$env:MAGPILOT_DEV_BYPASS_AUTH  = "true"
$env:ASPNETCORE_URLS            = "http://localhost:7088"
dotnet run --project src/Magpilot.Hub
# Open http://localhost:7088
```

Hot-reload UI work: `dotnet watch --project src/Magpilot.Web` does NOT
go through the hub's API. For end-to-end UI dev use the inspect skill
(`run-and-inspect`) against `Magpilot.Web` proxied to the hub, or
just rebuild SPA + restart hub.

### Dev loop with the installed agent

Once HENDRIK is on the installed scheduled-task agent (see "Windows
packaging" below), routine code iteration **must NOT** go through the
push -> CI -> publish -> install loop. That's the slow path; reserve
it for shipping a release.

For day-to-day dev:

1. **Stop the installed scheduled-task agent** before iterating:
   `Stop-ScheduledTask -TaskName MagpilotAgent` (or `Stop-Process -Id <pid>`).
   It's a normal MagpilotAgent task that owns TCP 5099, so leaving it
   running just blocks the port and locks `bin/` for rebuilds.
2. Edit + run from source: `dotnet run --project src/Magpilot.Agent` in
   an async PowerShell session (the same way pre-installer HENDRIK ran
   it). Same env vars as the table below; same port `:5099`.
3. Iterate, build, test, commit, push.
4. **Re-start the installed agent** when done:
   `Start-ScheduledTask -TaskName MagpilotAgent`. Only run
   `magpilot --magpilot-update` if a published release actually warrants
   pulling the new installer.

Same pattern in **magstronaut**: stop the installed agent on the dev
machine, point your local `dotnet run` at the same port, then restart
the installed task afterwards.

### Dev loop with the local hub (the end-to-end SPA iteration)

When iterating on SPA / hub behaviour locally (not against the LXC
hub), the workflow is **stop installed agent -> run local agent +
hub from source -> open `http://localhost:7088`**. Every step has a
specific gotcha that has already burned us in this session:

```pwsh
# 0. Stop the installed agent so port 5099 is free for our dev one.
Stop-ScheduledTask -TaskName MagpilotAgent

# 1. Run the agent (async pwsh session named "agent").
$env:MAGPILOT_AGENT_TOKEN      = "dev-token"
$env:MAGPILOT_AGENT_PUBLIC_URL = "http://127.0.0.1:5099"
$env:MAGPILOT_HUB_URL          = "http://127.0.0.1:7088"
$env:MAGPILOT_HUB_BEARER       = "dev-bearer"
$env:ASPNETCORE_URLS           = "http://localhost:5099"
dotnet run --project src/Magpilot.Agent

# 2. Build the SPA bundle into the hub's wwwroot (every SPA edit).
./scripts/build-hub.ps1

# 3. Run the hub (async pwsh session named "hub").
#    --no-launch-profile is REQUIRED -- without it Properties\launchSettings.json
#    overrides ASPNETCORE_URLS and the hub binds to :5154 instead of :7088.
#    --no-build skips rebuild when build-hub.ps1 already published.
$env:MAGPILOT_HUB_BEARER       = "dev-bearer"
$env:MAGPILOT_DEV_BYPASS_AUTH  = "true"
$env:ASPNETCORE_URLS           = "http://0.0.0.0:7088"
dotnet run --project src/Magpilot.Hub --no-launch-profile --no-build

# 4. Smoke test before opening the browser.
(Invoke-WebRequest -Uri "http://127.0.0.1:7088/healthz" -UseBasicParsing).StatusCode  # 200
```

**The orphan-process trap** -- this is the #1 thing to remember:

`dotnet run` execs a built exe (`Magpilot.Hub.exe` or
`Magpilot.Agent.exe`). When the parent pwsh session is killed
(`stop_powershell`, the session terminating, Ctrl-C-then-no-cleanup),
the spawned exe **stays alive as an orphan** and continues to hold
port 5099 / 7088 plus the data files in `bin/Debug/net9.0/data/`.
The next "run" attempt either:

* silently fails to bind and you get a "connection refused" /
  blank-page experience while logs lie that it's listening; or
* binds to a different port (the launch profile's fallback) so
  `localhost:7088` looks dead even though "something" is serving.

When something looks broken after a restart, ALWAYS check for
orphans first:

```pwsh
# Owner of the suspect port -- compare PID against your current shell's child.
Get-NetTCPConnection -LocalPort 7088 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
Get-Process -Id <pid> | Select-Object Id, ProcessName, StartTime
# If ProcessName is Magpilot.Hub / Magpilot.Agent and StartTime predates the
# shell you intended to run it from, it's an orphan. Kill it:
Stop-Process -Id <pid> -Force
```

Both processes show up under their real exe names (NOT "dotnet"),
because `dotnet run` execs the published exe. That makes them easy
to spot with `Get-Process -Name Magpilot.Hub` /
`Get-Process -Name Magpilot.Agent`.

Stable iteration loop after an SPA / hub edit:

1. `stop_powershell` for the `hub` shell.
2. `Get-Process -Name Magpilot.Hub` -- if a process exists, `Stop-Process -Id <pid> -Force`.
3. `./scripts/build-hub.ps1` (rebuilds Magpilot.Web into Magpilot.Hub/wwwroot AND publishes Magpilot.Hub).
4. Start a fresh `hub` async session with the env block above + `--no-launch-profile --no-build`.
5. Smoke-test `/healthz` before telling the user "ready to refresh".

For agent edits, same shape but with the `agent` shell + the agent
env block + `dotnet run --project src/Magpilot.Agent --no-build`.
The agent reads `launchSettings.json` too (we saw it override
`localhost:5062` -> `localhost:5099`) but it currently overrides
back to the right port via `ASPNETCORE_URLS`, so the
`--no-launch-profile` flag is optional for the agent. Don't rely on
that staying true; pass it if a future agent edit changes the
launch profile.

**When you push and forget to restart**: `build-hub.ps1` rebuilds the
SPA bundle on disk but a running hub serves the old bundle from its
in-memory static files cache + the bundle hash baked into the
already-served `index.html`. After every SPA change you MUST restart
the hub for the browser to see it -- a hard refresh alone won't help
if you skipped step (3) above.

### Deploying the hub to the LXC

`deploy/README.md` is the canonical recipe. Short version:

1. **CI builds + publishes the image** -- `.github/workflows/hub-image.yml`
   triggers on every push to `main` and on every `vX.Y.Z` tag, building
   linux/amd64 and pushing to `ghcr.io/chsienki/magpilot-hub` (public
   package). Tag conventions: `:main` + `:main-<sha>` on main pushes,
   `:0.1.11` + `:0.1` + `:latest` on tag pushes.
2. **Watchtower auto-pulls on the LXC** -- the `watchtower` service in
   `/srv/magpilot/docker-compose.yml` polls GHCR every 5 min for a
   newer digest of the `:latest` tag and recreates the hub container
   in place when one's available. Label-gated
   (`com.centurylinklabs.watchtower.enable=true` on the hub service)
   so accidental siblings can't auto-update.
3. **Force-an-update / pin a version** -- `docker compose pull hub && docker compose up -d hub`
   on the LXC for an immediate update; edit the `image:` line to
   `:0.1.10` (or any specific tag) for staging/rollback. Watchtower
   respects pinned tags, only auto-bumps `:latest`.
4. **Emergency local build** -- when CI is down or you're shipping an
   unreviewed patch, `docker buildx build -f src/Magpilot.Hub/Dockerfile -t ghcr.io/chsienki/magpilot-hub:emergency-<timestamp> --load .`
   then `docker save` -> `scp` -> `pct push 102` -> `docker load` ->
   point the compose file at the emergency tag.

**Critical `tar`/exclude gotcha when shipping source**: do NOT exclude
`**/wwwroot` blanket-style — that kills `Magpilot.Web/wwwroot/index.html`
which is real source. Only exclude `src/Magpilot.Hub/wwwroot/` (the
build output). The `.gitignore` reflects this.

**Critical `bin`/`obj` poisoning when building from magstronaut**:
the magstronaut root has a `.dockerignore` that excludes `**/bin/`
and `**/obj/`. Don't remove it. Without it, builds for satellites
that span both repos (e.g. `magnus-web`, whose `Dockerfile` build
context is the magstronaut root so it can `COPY` from the magpilot
submodule) ingest the local Windows-side `obj/` cache of
`Magpilot.UI`. Inside the container, `dotnet publish` ends up
**mixing fresh-compiled .wasm with stale cached scoped-CSS bundles**:
the rendered DOM has scoped attributes (b-XXXXXXXX) from the new
compile, but the served `Magpilot.UI.<hash>.bundle.scp.css` has
attributes from the old obj/ cache. Result: every scoped CSS rule
fails to match -- chat-view layout is gone, "no scrolling, wrong
sized message bars", silently. Hit on 2026-05-18; fixed by the
.dockerignore. Same trap doesn't bite the hub itself (it does
`dotnet publish` locally via `scripts/build-hub.ps1` and the Docker
image only ships the publish output), but the rule is worth keeping
in mind if you ever add another satellite that builds via Docker
from the magstronaut root.

The agent runs on each host directly (no docker). HENDRIK runs the
**installed `MagpilotAgent` scheduled task** (registered by
`installer/magpilot.iss` at user logon, NOT SYSTEM, so `~/.copilot/`
is reachable). See `installer/README.md` for the install + upgrade
recipe; `magpilot --magpilot-update` handles in-place upgrades. The
older "run as `dotnet run` in an async pwsh session named `agent`"
pattern is the dev-loop, NOT the deployed state -- see "Dev loop
with the installed agent" above.

## Environment variables

| Var | Where | Purpose |
|---|---|---|
| `MAGPILOT_AGENT_TOKEN` | agent | The agent's own bearer secret for inbound calls from the hub. **Per-agent** (V2a of magpilot-pairing): minted on enrollment by the hub's voucher-redeem flow; the launcher writes it into `magpilot.env` and the hub stores the matching value in `agents.token`. The hub does NOT read this env var anymore -- it looks up the per-agent token from its own database, keyed by agent name. |
| `MAGPILOT_AGENT_PUBLIC_URL` | agent | URL the agent broadcasts in UDP discovery so the hub can call back. |
| `MAGPILOT_AGENT_NAME` | agent (optional) | Source label used for central log forwarding. Defaults to hostname. |
| `MAGPILOT_HUB_URL` | agent (optional) | Hub base URL the agent posts forwarded logs to (e.g. `http://192.168.1.239:7088`). Forwarder is a no-op if unset. |
| `MAGPILOT_HUB_BEARER` | hub + non-cookie clients | Bearer secret the hub validates for API calls without a session cookie (agents, sidecars, curl, MAUI app). Required for `/api/log` ingest from non-SPA sources. **In active use** -- set it. |
| `MAGPILOT_AUTO_APPROVE` | agent (optional) | When `"true"`, the agent auto-picks an allow-flavored option for every Copilot `session/request_permission` callback (prefers `allow_always`, falls back to `allow_once`, then any "allow" option). Intended for always-on autonomous agents like Magnus where `/quick-prompt` callers (WhatsApp, cron) have no human to click "approve". Without it, permission requests fan out to SSE subscribers that have no UI to answer, and time out after 5 minutes to a deny. **Don't set this on agents that share a host with the user** (e.g. HENDRIK); only on dedicated agent containers where the trust boundary is the container itself. Superseded for granular use by the per-session yolo toggle (see `MAGPILOT_YOLO_DISABLED` below) but still honoured for backward compat. |
| `MAGPILOT_YOLO_DISABLED` | agent (optional) | When `"true"`, the per-session yolo toggle is refused with 403; the SPA greys out the YOLO switch and shows a tooltip explaining why. The legacy `MAGPILOT_AUTO_APPROVE` env var is unaffected (it's a separate code path). Set this on user-account agents like HENDRIK where the agent runs with the user's full permissions and unattended auto-approve would be dangerous; leave unset (default-allow) on dedicated container agents like Magnus. |
| `MAGPILOT_DEV_BYPASS_AUTH` | hub | When `"true"`, skips OAuth (dev only -- redirects `/login` to `/dev-login`). |
| `MAGPILOT_HUB_DATA` | hub | Directory for `hub.db` and `logs.db`. Defaults to `./data`. |
| `MAGPILOT_HUB_TRUSTED_PROXIES` | hub | Comma list of IPs allowed to set `X-Forwarded-*` (NPM IP). Defaults to `127.0.0.1,::1` -- **required** when NPM and the hub run as containers on the same LXC under `network_mode: host`, since NPM's request reaches the hub from `127.0.0.1` not the LXC's external IP. Without this, `X-Forwarded-Proto: https` is dropped, OAuth `redirect_uri` becomes `http://...` and GitHub rejects. |
| `MAGPILOT_HUB_COOKIE_DOMAIN` | hub (optional) | Sets the auth cookie's `Domain` attribute. Use a leading-dot value like `.home.sienkiewi.cz` to share sign-in across satellite SPAs hosted on sibling subdomains (e.g. magnus.home.sienkiewi.cz). Leave unset for plain dev runs at localhost. |
| `OAUTH_CLIENT_ID` / `OAUTH_CLIENT_SECRET` | hub | GitHub OAuth App credentials. Without these the hub serves `/login -> 'OAUTH_CLIENT_ID not configured'`. |
| `OAUTH_ALLOWED_GITHUB_USERS` | hub | Comma list of GitHub logins the hub allows. Anyone else gets denied after OAuth. |
| `MAGPILOT_HUB_PUBLIC_URL` | hub (optional but recommended) | Externally-reachable URL of the hub (e.g. `https://magpilot.home.sienkiewi.cz`). Used by the pairing endpoint (`GET /api/admin/enroll/bundle`) to embed the right hub address in the enrollment bundle. When unset, the service falls back to the first non-wildcard listen URL, then to `http://<lan-ip>:<port>` -- both fine for LAN-only deployments, but a NPM-fronted prod hub should set this so bundles don't leak the unencrypted internal URL. |
| `MAGPILOT_RELEASE_REPO` | hub (optional) | GitHub `owner/repo` the hub's `ReleaseTracker` polls for the latest magpilot release. Defaults to `chsienki/magpilot`. |
| `MAGPILOT_GITHUB_TOKEN` | hub (optional) | Bearer token for the GitHub API call. **Required** if the configured release repo is private -- anonymous calls to `releases/latest` return 404 against private repos, which ReleaseTracker logs as a Warning in `/admin/logs`. Also bumps the unauthenticated 60 req/h limit to 5,000 req/h. |
| `MAGPILOT_AGENT_URL` | magpilot launcher | Default `http://127.0.0.1:5099`. Where the wrapper reaches its local agent. |
| `MAGPILOT_REAL_COPILOT` | magpilot launcher (optional) | Explicit path to the real `copilot` binary. Useful when the wrapper is on PATH and you want to override the autodetect of the real copilot binary. |
| `MAGPILOT_ENV_FILE` | agent + launcher (optional) | Explicit path to `magpilot.env` instead of the installer-layout default (`<install>\config\magpilot.env`). The agent reads it on startup; the launcher's `--magpilot-pair=<bundle>` writes to it. When set, `--magpilot-pair` skips the scheduled-task bounce so dev / test runs don't accidentally kill an unrelated installed agent. |
| `MAGPILOT_TERM_BACKGROUND` | magpilot launcher (optional) | `auto` (default), `dark`, or `light`. Controls the `COLORFGBG` hint the launcher passes to copilot so its TUI themes for the right background. See "Terminal theming" below. |
| `MAGPILOT_TERM_ENABLE_GITHUB_THEME` | magpilot launcher (optional) | `1` (default) / `0`. When on, sets `COPILOT_GITHUB_THEME=1` for the child so copilot's GitHub colour mode is selectable in its own `/theme` picker. |
| `MAGPILOT_TERM_THEME` / `MAGPILOT_TERM_THEME_FILE` | magpilot launcher (optional) | Name of a palette file at `<install>\config\themes\<name>.json`, or an explicit path. Enables ANSI-palette colour overrides. See "Terminal theming" below. |

## Windows packaging + autoupdate

The repo ships a Windows installer + a hub-mediated autoupdate path.
The whole story is documented in detail in `installer/README.md`; the
short version that matters for AI agents working on this repo:

### Versioning

- `VERSION` (top-level text file) is the **single source of truth** for
  the release semver. One line, e.g. `0.1.0`. Same pattern as
  `chsienki/WhichBox`.
- `Directory.Build.props` reads it and propagates `<Version>` +
  `<InformationalVersion>` to every project. Every assembly stamps the
  same version. Inspect with
  `(Get-Item bin/.../X.dll).VersionInfo.ProductVersion`.
- `Magpilot.Shared.Versioning` exposes `AssemblyVersion` (strips the
  `+gitsha` suffix the SDK appends) and `ProtocolVersion` (an int that
  bumps **only** on incompatible wire-contract changes; baseline is 1).
- Bumping a release is a deliberate act: edit `VERSION`, commit,
  `git tag vX.Y.Z`, `git push --tags`. The release workflow validates
  the tag matches `VERSION` and refuses to build if they're out of sync.
  No bump-version script needed.

### Update flow

```
GitHub Releases <--(every 1h)-- Hub.ReleaseTracker --> ReleaseCache
                                                          |
              GET /api/agent-version?from=<ver>           |
   Agent.UpdatePoller (every 15min) <-----<--<--<--<--<--+
            |
            v
       LatestVersionCache
            |
            v
   GET /api/version/latest (NO auth) <----- magpilot launcher
                                            (every invocation: 500ms timeout,
                                             prints banner if updateAvailable;
                                             also drives --magpilot-update)
```

Endpoints added by the packaging work:

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/version` (agent) | none | Agent's own `{version, protocolVersion}` |
| `GET /api/version/latest` (agent) | none | Hub-reported latest, cached locally |
| `GET /api/agent-version?from=X.Y.Z` (hub) | cookie or bearer | Hub's view of latest release; computes `updateAvailable` for the caller |

The agent's version endpoints are deliberately **unauthenticated** so
the launcher can show its banner without `MAGPILOT_AGENT_TOKEN` set.

### Launcher subcommands

| Flag | Purpose |
|---|---|
| `magpilot --magpilot-version` | Print local + agent-reported version info |
| `magpilot --magpilot-update` | Download + run the latest installer silently (validates SHA256 against the GitHub release asset) |
| `magpilot --magpilot-pair=<bundle>` | Pair the agent with a hub. `<bundle>` is copied from the hub's `/admin/enroll` page; the launcher decodes it, upserts the three keys into `magpilot.env`, and bounces the installed scheduled task. |
| `magpilot --magpilot-help` | Wrapper-only flag help |

The banner check fires on **every** non-help, non-skip-check invocation
with a 500ms timeout. Failures are silent. See `Magpilot.Host/UpdateBanner.cs`.

### Terminal theming (local client)

The launcher wraps copilot in a ConPTY (`Magpilot.Host/PtyHost.cs`). copilot
renders its own TUI, so the launcher only influences colours three ways:
**environment hints** it passes to the child, **OSC sequences** it injects
into the real terminal around copilot's output, and **byte-stream rewrites**
of copilot's output as it flows through the PTY. It never writes copilot's
own config -- env + the byte stream is the clean ring boundary.

Three launch paths, with different reach:

- **ConPTY host** (agent coordination) and **PTY passthrough**
  (`Program.RunPassthroughAsync`, used when there's no agent token or the
  agent is unreachable, so plain `magpilot` works standalone): full theming
  including the byte-stream rewrites.
- **direct-exec** (`--magpilot-skip-check`, or any redirected-stdio case):
  env + OSC theming only -- copilot owns the TTY directly, so there's no
  stream for the launcher to rewrite. (`TerminalTheming.cs` is the shared
  env+OSC applier for all paths.)

What happens at spawn (helpers: `TerminalColor.cs` / `TerminalThemeConfig.cs`
/ `TerminalBackgroundProbe.cs` / `AnsiColorRewriter.cs`):

1. **Background detection -> `COLORFGBG`.** copilot's default colour mode is
   `auto`, which emits `OSC 11` (`ESC]11;?`) to ask the terminal for its
   background and adapt dark/light. **Under the ConPTY** that query never
   reaches the outer terminal, so copilot times out and mis-themes; the host
   runs the OSC 11 probe itself, computes dark/light from the reply's
   luminance, and passes copilot the answer via `COLORFGBG` (copilot's
   documented fallback). **In direct-exec** copilot inherits the real
   terminal and its own probe works, so we do NOT probe and only pin
   `COLORFGBG` when the user forced `MAGPILOT_TERM_BACKGROUND=dark|light` --
   and note copilot's live OSC 11 wins over `COLORFGBG` there, so to force a
   background in passthrough use the theme file's `background` (which actually
   recolours the terminal) rather than the pin.
2. **Palette overrides (`OSC 4`/`10`/`11`).** A theme JSON at
   `<install>\config\themes\<name>.json` (selected by `MAGPILOT_TERM_THEME`,
   or an explicit `MAGPILOT_TERM_THEME_FILE`) remaps ANSI-16 palette entries +
   fg/bg on the real terminal at startup and resets them (`OSC 104/110/111`)
   on exit. This recolours copilot's base-16 (`default`) theme output;
   truecolor themes emit fixed RGB and are unaffected. Shape:
   `{ "palette": { "0": "#1e1e1e", "4": "#3b8eea" }, "foreground": "#d4d4d4", "background": "#1e1e1e", "thinking": "#7c8a8a", "inputBand": "#073642" }`.
3. **GitHub theme flag.** `COPILOT_GITHUB_THEME=1` is set by default so the
   GitHub colour mode is a pickable option in copilot's own `/theme`.
4. **Byte-stream rewrites (`AnsiColorRewriter`, PTY paths only).** Two things
   copilot renders that the palette can't reach, each opt-in via a theme-file
   key:
   - `"thinking"`: copilot draws reasoning text with the terminal's **faint**
     attribute (SGR 2, ~half the foreground brightness -- unreadable on dark
     backgrounds, and not a palette colour). The rewriter turns SGR `2` into
     `38;2;R;G;B` (the thinking colour at full intensity) and SGR `22` into
     `22;39`. This decouples the reasoning colour from the foreground.
   - `"inputBand"`: copilot's composer surface (`backgroundSecondary`) is a
     fixed grey `#202020` from copilot's own ramp, not the terminal palette.
     It appears as a background (the fill) and as a foreground (the halfblock
     box edges), so the rewriter retargets **both** `48;2;32;32;32` and
     `38;2;32;32;32` to the input-band colour. Only `#202020` is matched, so
     diff / selection / link colours are untouched (`#202020` is too dark to
     ever be real text). The rewriter is an incremental SGR parser that
     survives escapes split across read buffers; it's careful to skip the
     literal `2`/`5` inside `38;2;...` / `38;5;...` selectors.

`COLORTERM=truecolor` is also forced (has been) so the child emits 24-bit RGB
instead of downgrading to bright-16 under an empty ConPTY `COLORTERM`.

**Diagnostics for tuning a theme against copilot's real output:**
`MAGPILOT_TERM_DUMP=<path>` tees copilot's raw (pre-rewrite) output to a file;
`MAGPILOT_TERM_DUMP_POST=<path>` tees what actually reaches the terminal
(post-rewrite). Both are in the PtyHost output pump. Parse them for `48;2;` /
`38;2;` sequences to find the exact colours copilot emits for an element.

Pure logic (OSC parsing, luminance, sequence generation, theme-file parsing,
the SGR rewriter) is unit-tested in `tests/Magpilot.Host.Tests` (not in
`Magpilot.slnx`; run `dotnet test tests/Magpilot.Host.Tests`). An integration
test drives a stub through a real ConPTY to confirm `COLORFGBG` reaches the
child.

### Installer

- `installer/magpilot.iss` (Inno Setup, per-machine, admin install).
- `installer/{install-task,uninstall-task,firewall}.ps1` (helpers).
- Components: `Magpilot Agent`, `Magpilot Launcher`. Tasks: PATH,
  scheduled task at user logon, firewall rules.
- Custom Settings page collects hub URL + agent token + public URL,
  writes them to `%ProgramFiles%\Magpilot\config\magpilot.env`. On
  upgrade (silent or interactive) the existing file is read and values
  are pre-populated, so `magpilot --magpilot-update` preserves them.
- The agent runs as a **scheduled task at user logon** (NOT SYSTEM),
  so `~/.copilot/` is reachable. Task name: `MagpilotAgent`.
- `install-task.ps1` resolves which user to register the task for via
  a chain: `-User` parameter (the installer passes this from
  `GetEnv("USERNAME")/GetEnv("USERDOMAIN")` when those name a real
  user) -> `Win32_ComputerSystem.UserName` (console-logged-in) ->
  `quser` active interactive session -> `$env:USERDOMAIN\$env:USERNAME`
  IF NOT a machine account -> exit 2 with a clear error. The
  machine-account refusal matters because `Register-ScheduledTask
  -UserId MACHINE$` fails with "No mapping between account names and
  security IDs" and Inno's Exec swallows the exit code, so the
  installer would otherwise "succeed" while leaving the agent
  unscheduled. See `installer/README.md` -> "Headless / SSH-driven
  install" for the workarounds.

### Quick-install bootstrap (`scripts/install.ps1`)

The one-liner install path documented in the README:

```pwsh
irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1 | iex
```

Lives at `scripts/install.ps1`. Logic:

1. Fetch `version.json` from `/releases/latest/download/` (302-redirected
   to the most recently published non-draft release) to discover the
   current semver. No GitHub API token required.
2. Build the installer + checksum asset URLs from the tag (`v<semver>`).
3. Download both to a unique `%TEMP%\magpilot-install-<rand>\` dir.
4. Verify SHA256 -- bail if it doesn't match the `.sha256` file.
5. `Start-Process -Verb RunAs` to launch the installer with UAC
   elevation. `-Silent` adds `/SILENT` for unattended runs.
6. Preserve temp artifacts on success or failure so a partial install
   can be re-run without re-downloading.

Parameters (use the scriptblock form to pass them through `irm | iex`):

```pwsh
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/chsienki/magpilot/main/scripts/install.ps1))) -Silent
```

* `-Repo <owner/repo>` -- override the source repo (forks, mirrors).
* `-Version <semver>` -- pin to a specific version instead of latest.
* `-Silent` -- pass `/SILENT` to Inno; wizard runs unattended.
* `-DryRun` -- download + verify only; don't run.

**Failure modes worth knowing:**
* If the latest release is still a **draft**, `releases/latest/download/`
  returns 404 (same trap as the hub's autoupdate flow). Publish the
  draft (`gh release edit vX.Y.Z --draft=false`) before pointing users
  at the one-liner.
* If the repo is **private**, the redirect target requires auth that
  `Invoke-WebRequest`'s anonymous call can't supply. The script
  doesn't try to handle this; use the manual download flow off the
  Releases page instead, or fork to a public mirror and point
  `-Repo` at it.

### Pairing V2a / V2b / V3 (`magpilot --magpilot-pair`)

The installer is purely files + scheduled task -- no wizard pairing
fields. After install completes, install-task.ps1 invokes
`magpilot --magpilot-pair` (V3 interactive discovery) in a visible
console; the launcher UDP-broadcasts to find a hub, submits a
pairing claim, opens the user's browser to
`/admin/agents?pending=<id>` for the admin to click Adopt. Three
flows live side by side, all converging on
<see cref="MagpilotPairWriter"/> for the env-file write + task
bounce:

* **V3 interactive (the default, installer-triggered):**
  `magpilot --magpilot-pair` -- no argument. UDP-discover hubs,
  pick one, claim, browser, long-poll, write env. Agent name is
  `Environment.MachineName` (overridable via
  `MAGPILOT_AGENT_NAME`).
* **V2a non-interactive bundle paste:**
  `magpilot --magpilot-pair=<magpilot2+...>` -- bundle copied from
  the hub's `/admin/enroll` page. Decodes -> POST
  `/api/enroll/redeem` -> writes env. Bypasses UDP entirely; useful
  for scripted deployments where the agent has no human at the
  console.
* **V2b revocation:** `/admin/agents` admin page lists paired
  agents + per-row Revoke button. Revoked agents render with 410
  Gone from the Proxy wrapper; re-pair via V3 or V2a restores them
  (`revoked_at = NULL` in the agents row upsert).

V2a uses **voucher-based enrollment**: hub-side
<see cref="EnrollmentService.CreateVoucher"/> mints a 15-minute
single-use voucher; agent's launcher posts the voucher to
`/api/enroll/redeem`; hub atomically check-and-consumes + mints a
fresh per-agent token + upserts the agents row + returns the token.

V3 inverts the secret-generation direction: agent-generated claim
secret, admin-confirmed approval. The hub-side
<see cref="ClaimService.CreateClaim"/> stores `SHA256(secret)` + a
6-char fingerprint (last 6 chars of the secret, public hint) with
a 5-min TTL. The launcher POSTs to `/api/enroll/claim`; the
launcher's long-poll against `/api/enroll/claim-status?secret=...`
blocks server-side up to 60s, signaled immediately by
Approve/Reject via an in-process `TaskCompletionSource` keyed by
claim id. Approval reuses
<see cref="EnrollmentService.MintTokenForClaim"/> -- the same
upsert path as voucher redeem -- so the agents-table side stays
identical across the two enrollment flavors.

There's no shared `MAGPILOT_AGENT_TOKEN` on the hub anymore. The
hub looks up per-agent bearers from its own database keyed by
agent name. An agent that hasn't been paired is unreachable from
the hub; the act of pairing IS what registers the agent + its
bearer with the hub.

**V3 wire protocol:**

* UDP/47824 (distinct from the agent-side discovery on 47823 so
  both can co-exist on one machine). Magic string
  `MAGPILOT-PAIR-DISCOVER-v1`. Always-on listener
  (`PairingDiscoveryResponder` background service in the hub);
  no enable/disable button -- the auth gate is the
  admin-approve step in the SPA, not discovery.
* Hub's UDP reply carries `{ magic, hubUrl, hubName }`. URL
  resolution follows the same chain as
  `EnrollmentService.ResolveHubPublicUrl` plus a "match my LAN
  IP to the probing agent's subnet" fallback when the bound
  listen URL is a wildcard.

**V3 HTTP endpoints (claim flow):**

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/enroll/claim` | NONE (secret IS the auth) | Body `PairingClaimRequest{Secret, AgentName}`. Stores `SHA256(secret)` + 6-char fingerprint + 5min TTL. Returns `PairingClaimResponse{ClaimId, ApproveUrl, Fingerprint}`. |
| GET | `/api/enroll/claim-status?secret=...` | NONE | Long-poll (~60s server-side). Returns `PairingClaimStatus{Status, AgentToken?}`. Status is `Pending` on timeout (launcher re-polls), terminal otherwise. |
| GET | `/api/admin/agents/claims` | cookie | `PairingClaimSummary[]` for the SPA's pending-requests section. |
| POST | `/api/admin/agents/claims/{id}/approve` | cookie | Atomic: verify pending + non-expired; mint per-agent token via `MintTokenForClaim`; upsert agents row; mark consumed; signal waiters. 404 if not found / already-decided / expired. |
| POST | `/api/admin/agents/claims/{id}/reject` | cookie | Mark rejected; signal waiters. 404 if not pending. |

**V3 DB schema:**

```sql
CREATE TABLE claims (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    secret_hash BLOB NOT NULL UNIQUE,
    agent_name TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    agent_token TEXT,
    decided_at INTEGER,
    decided_by_user TEXT
);
```

Same hash-only discipline as the V2a `vouchers` table -- the hub
never holds the plaintext claim secret. Status is stored as a
string for legibility; `PairingClaimState` enum on the wire is
`[JsonStringEnumConverter]`-serialized to match the codebase
convention for other enums.

The `agents.enrolled_via` column gets the claim id (vs voucher id
in the V2a path). The two id spaces don't share a counter so the
column is strictly ambiguous between voucher / claim sources --
acceptable for V3 (the column is audit metadata; security boundary
is the per-agent token in `agents.token`). A future schema bump
could add `enrolled_via_kind` if disambiguation matters.

**V3 launcher discovery loop:**

`MagpilotPairDiscover.RunAsync` (`src/Magpilot.Host/MagpilotPairDiscover.cs`):

1. Broadcast `MAGPILOT-PAIR-DISCOVER-v1` 3 times across a 3-second
   window. Concurrent listener collects unicast replies into a
   `Dictionary<HubUrl, DiscoveredHub>` (dedup).
2. No hubs found -> exit code 4 with hint to use
   `--magpilot-pair=<bundle>` instead.
3. One hub -> auto-pick. Multiple -> console picker (`[1] HENDRIK
   (http://...)` / `[c] cancel`).
4. Generate CSPRNG 32-byte secret (base64url-encoded, 43 chars).
5. POST `/api/enroll/claim`. Print fingerprint + approve URL.
   `Process.Start(approveUrl) { UseShellExecute = true }` for
   browser open (silent failure logs the URL for manual open).
6. Long-poll `/api/enroll/claim-status?secret=...` (5-min total
   budget; each HTTP call has a 90-second timeout so the 60-second
   server-side block has slack). Status transitions:
   - `Approved` -> write env via `MagpilotPairWriter.UpsertEnv`,
     bounce task, exit 0.
   - `Rejected` -> exit 7.
   - `Expired` -> exit 7 with "no decision within 5 minutes" hint.
   - `Pending` -> re-poll.
7. Same `MAGPILOT_ENV_FILE`-aware task-bounce skip as the V2a
   path so dev / test runs don't kill the installed agent.

**V3 SPA admin page:**

`Pages/AgentsAdmin.razor` mounts at `/admin/agents`. Shows two
sections:

* "Pending pair requests" at the top -- one card per claim with
  agent name + fingerprint chip + expiry countdown + Adopt /
  Reject buttons. Reads `?pending=<id>` from the URL so the
  freshly-redirected browser visually emphasizes the right card
  via `mud-elevation-4`. 5-second poll keeps it live without page
  refresh.
* "Paired agents" below -- V2b MudTable with name / status / url
  / enrolled / last-seen + per-row Revoke. Revoked rows greyed.

**Gotcha (carried from V2a / V2b)**: running `--magpilot-pair`
without `MAGPILOT_ENV_FILE` set on a machine where the installed
`MagpilotAgent` task is configured WILL bounce the installed
agent. If a `dotnet run --project src/Magpilot.Agent` dev agent is
bound to `:5099`, the installed task will fight for the port and
one will lose. Always set `MAGPILOT_ENV_FILE` to a throwaway path
when testing pair from source.

**V2b revocation specifics:**
agent's per-agent token in the database + in-memory; subsequent
hub-to-agent calls return 410 Gone with a "re-pair with a fresh
voucher" hint from the Proxy wrapper. Fully reversible: re-running
`magpilot --magpilot-pair=<fresh-bundle>` on the agent upserts
`revoked_at = NULL` and writes a new token.

**Voucher lifecycle:**

1. Authenticated SPA visitor opens `/admin/enroll` and clicks "Create
   voucher". The SPA POSTs to `/api/admin/enroll/voucher`; the hub
   mints a 32-byte CSPRNG secret, stores only its SHA256 (the
   `vouchers` table never holds plaintext), sets a 15-minute TTL,
   and returns an `EnrollmentBundle{ HubUrl, Voucher, HubBearer }`
   encoded as `magpilot2+<base64url(JSON)>`.
2. User copies the bundle, runs `magpilot --magpilot-pair=<bundle>`
   on the fresh agent machine.
3. Launcher decodes, POSTs `/api/enroll/redeem` (UNAUTHENTICATED --
   the voucher IS the auth) with `{ voucher, agentName }`. Hub does
   an atomic check-and-consume inside a single transaction:
   verifies the hash matches an issued voucher, verifies not
   expired, verifies not already consumed. On success: mints a
   fresh 32-byte agent token, marks the voucher consumed, upserts
   the agent row with the new token, returns `{ agentToken }`.
4. Launcher writes `MAGPILOT_HUB_URL`, `MAGPILOT_AGENT_TOKEN`
   (minted), `MAGPILOT_HUB_BEARER` into `magpilot.env` (in-place
   upsert preserving comments + unrelated keys; atomic temp + move),
   then bounces the installed `MagpilotAgent` scheduled task.

**Failure semantics on `/api/enroll/redeem`:**

| Status | Cause |
|---|---|
| 200 | Success: `{ agentToken }` in body. |
| 400 | Missing `voucher` or `agentName`. |
| 401 | Voucher hash doesn't match any issued. |
| 410 Gone | Voucher expired OR already consumed. Body's `error` field distinguishes. |

The launcher maps each non-200 to a user-facing hint that includes
"generate a fresh one on the hub's /admin/enroll page" so the recovery
action is obvious.

**Bundle wire format** -- defined in `EnrollmentBundle.cs` in
`Magpilot.Shared`:

```
magpilot2+<base64url(JSON)>
```

JSON payload: `{ hubUrl, voucher, hubBearer }`. **No agentToken in
the bundle** -- it's minted on redeem. V1's `magpilot1+` format is
gone; the V2 launcher only decodes `magpilot2+`. The agent's
`MAGPILOT_AGENT_PUBLIC_URL` is also not in the bundle:
machine-specific, `DiscoveryResponder.ResolveSelfUrl` auto-detects
from the hub's source IP when unset.

**Database** -- the V2a schema lives in `hub.db`:

* `vouchers (id, secret_hash, created_at, expires_at, consumed_at, consumed_by_agent_name, created_by_user)` -- the hub stores only `SHA256(secret)`, never plaintext.
* `agents` gains `enrolled_at`, `enrolled_via` (FK to `vouchers.id`), `revoked_at` (null = active). Per-agent token still lives in `agents.token` as it always did; V2a just stops sharing a single value.
* `AgentRegistry.InitDb` runs the migration idempotently via the `AddColumnIfMissing` helper (probes `PRAGMA table_info` and only runs the `ALTER` when the column is genuinely absent) -- safe to upgrade in place. Use this same helper for any new agents / vouchers / claims column; do not write bare `ALTER TABLE` statements in `InitDb`.

**MagpilotPair.RunAsync** (`src/Magpilot.Host/MagpilotPair.cs`):

1. Decode `magpilot2+` bundle.
2. Redeem the voucher via `POST /api/enroll/redeem` against
   `bundle.HubUrl` BEFORE touching disk -- a failed redeem (the
   most common failure mode) doesn't leave a half-updated
   `magpilot.env`.
3. Locate `magpilot.env` (same chain as `EnvFileLoader`):
   `MAGPILOT_ENV_FILE` override -> `<launcher-exe>/../config/magpilot.env` ->
   `%ProgramFiles%\Magpilot\config\magpilot.env`.
4. Upsert the three keys in place (preserve comments, blanks,
   unrelated key/values). Atomic write via temp + move.
5. On Windows, bounce the installed `MagpilotAgent` scheduled task
   via `schtasks.exe`. **Skipped when `MAGPILOT_ENV_FILE` is set**
   so a dev / test run on a custom env file doesn't kill an
   unrelated installed agent.

**Gotcha that bit a dev run**: running `--magpilot-pair` without
`MAGPILOT_ENV_FILE` set on a machine where the installed
`MagpilotAgent` task is configured WILL bounce the installed agent.
If a `dotnet run --project src/Magpilot.Agent` dev agent is bound
to `:5099`, the installed task will fight for the port and one
will lose. Always set `MAGPILOT_ENV_FILE` to a throwaway path
when testing pair from source.

**V2b revocation specifics:**

* `AgentRegistry.Revoke(name)` is the single mutation entry point:
  in-memory `_tokens.Remove(name)` (so `GetToken` returns null
  immediately, no race window where the next call would still
  succeed) + DB `UPDATE agents SET revoked_at = $now, token = NULL`.
  Logged at Warning so revocations show up in `/admin/logs`.
* `Proxy(name, reg, ...)` in `HubEndpoints` short-circuits with 410
  Gone BEFORE attempting the call when `reg.IsRevoked(name)`. The
  body's `revoked: true` field lets the SPA distinguish "this is a
  policy decision" from "agent is offline / unreachable" (which
  surface as 502).
* `AgentRegistry.Upsert` (called by the discovery prober every
  60s) preserves `RevokedAt` from the existing in-memory record,
  so re-discovering a revoked agent doesn't accidentally
  un-revoke it. Same for `EnrolledAt`.
* The redeem path's `revoked_at = NULL` in the agents UPSERT is
  what makes re-pair the canonical "restore" action. After a
  successful redeem, `EnrollmentService.RedeemVoucher` calls
  `AgentRegistry.Reload(name)` to pick up the freshly-cleared
  `revoked_at` (and the new token + enrolled_at). Using `Upsert`
  there instead would clobber the columns we just wrote in the
  transaction.

**V3 installer integration:**

* `installer/magpilot.iss` has **NO wizard pairing fields**. Pairing
  is post-install, not at install time. The installer just lays
  down files + registers the scheduled task; the legacy V2b
  `PairingPage` (single optional bundle text field) and the
  matching `-Bundle` parameter on `install-task.ps1` are both gone.
* `install-task.ps1` unconditionally shells out to
  `<install>\bin\magpilot.exe --magpilot-pair` (V3 interactive
  discovery) in a visible console after scheduled-task
  registration. The user's browser opens to
  `/admin/agents?pending=<id>`; the launcher long-polls until the
  admin clicks Adopt, then writes `magpilot.env` and bounces the
  task. Non-zero exit logs a `Write-Warning` but doesn't fail the
  install -- the user can re-run `magpilot --magpilot-pair` from
  any shell to retry.
* Pascal-side surface is intentionally minimal -- no UDP, no HTTP
  POST, no bundle decoding. Inno Setup's Pascal Script has no
  native UDP and only `DownloadTemporaryFile` (GET) for HTTP, so
  doing real pairing work in Pascal would mean shipping a parallel
  HTTPS + UDP stack. The launcher does everything in C# instead;
  Pascal just invokes it.
* For unattended scripted installs (no human at the console at
  install time), the V2a bundle path is still available:
  `magpilot --magpilot-pair=<magpilot2+...>` run manually
  post-install. Generate the bundle from the hub's `/admin/enroll`
  page. Not wired into the installer wizard.

### Reusable hub-side patterns (extend, don't reinvent)

These primitives crystallized during the pairing work and now
own their respective concerns. New features should extend them
rather than spawn parallel implementations.

* **`AgentRegistry.AddColumnIfMissing(conn, table, column, type)`** --
  the idempotent schema-migration helper. Probes `PRAGMA
  table_info`, only runs the `ALTER TABLE` when the column is
  genuinely absent. Used three times in `InitDb` today
  (`enrolled_at`, `enrolled_via`, `revoked_at`); any new
  agents / vouchers / claims column goes through here. Default-NULL
  semantics mean pre-migration rows stay valid, so you can ship
  the column without a downtime. Don't write bare `ALTER TABLE`
  in `InitDb` -- it'll throw on the second startup.

* **`Proxy(name, reg, ...)` in `HubEndpoints`** -- the single
  choke point for every per-agent-name HTTP route on the hub.
  Already gates revocation (returns 410 Gone with `revoked: true`
  when `reg.IsRevoked(name)`). Any future per-agent policy
  decision (visibility filtering for multi-user, RBAC,
  rate-limiting, agent-status preconditions) belongs here -- one
  decision point that returns the right status code consistently.
  Per-route guards duplicated across the dozen `/api/agents/...`
  endpoints will drift; the wrapper exists precisely so they
  don't have to.

* **`TaskCompletionSource<bool>` keyed by entity id** -- the
  hub-side push-style long-poll pattern, demonstrated in
  `ClaimService` for claim-status. The waiter awaits the TCS with
  a server-side timeout (~60s); the mutator (Approve/Reject)
  completes the TCS via a `ConcurrentDictionary<int, TCS>` keyed
  by claim id, so a status flip wakes the waiter within ~30ms
  instead of waiting for the next poll tick. Same shape works
  for any "user did a thing in the SPA, agent / launcher needs to
  know right now" flow. In-process only -- if magpilot ever
  grows to multiple hub replicas this graduates to Redis
  pub/sub or similar, but that's a long way off.

* **`MagpilotPairWriter.UpsertEnv` + `TryRestartScheduledTaskAsync`**
  (`src/Magpilot.Host/MagpilotPairWriter.cs`) -- the agent-side
  helpers shared by `MagpilotPair` (V2a bundle path) and
  `MagpilotPairDiscover` (V3 interactive path). Any future pair
  flow that needs to atomically upsert keys into `magpilot.env`
  + bounce the installed `MagpilotAgent` scheduled task uses
  these. Don't duplicate the temp+move write, the env-file
  resolution chain, or the `MAGPILOT_ENV_FILE`-set-skip-bounce
  guard -- extend the helper instead. The guard exists so a dev
  run with a throwaway env file doesn't accidentally bounce an
  unrelated installed agent (see the "Gotcha" above the V2b
  revocation specifics).

### Autoupdate visibility: two ways the chain silently breaks

The hub-mediated autoupdate path relies on `releases/latest` returning
a non-draft, non-prerelease release for the configured
`MAGPILOT_RELEASE_REPO`. Two failure modes have actually bitten us:

- **Repo is private + hub has no `MAGPILOT_GITHUB_TOKEN`** -- GH API
  returns 404 to anonymous callers against private repos.
  `ReleaseTracker` used to log this at Information (invisible in
  `/admin/logs`); now it logs at Warning so it shows up. Either set
  `MAGPILOT_GITHUB_TOKEN` on the hub or make the repo public.
- **All releases are drafts** -- `releases/latest` skips drafts by
  design, returns 404 even with auth. Symptom: hub returns
  `latestVersion: ""`, no update banner ever fires, every install in
  the field silently lags HEAD. Publish the draft (`gh release edit
  vX.Y.Z --draft=false`).

When in doubt: `Invoke-RestMethod 'http://192.168.1.239:7088/api/agent-version'
-Headers @{Authorization='Bearer <HUB_BEARER>'}` -- if `latestVersion`
is `""`, the hub doesn't see a release; check `/admin/logs?level=Warning`
for the ReleaseTracker explanation.

Wire-contract gotcha that compounds this: an agent that's been
stuck on an old version may also be running an OLD wire shape. e.g.
`/api/sessions/{id}/history` was `IReadOnlyList<HistoryEntry>` pre-
`cd323a6` and `HistoryPage` after. The SPA pinned to the new shape
crashes on the old. So a quietly-broken autoupdate path doesn't
just leave you missing features -- it can crash live sessions when
the SPA moves ahead of the agent. The longer-term fix is to bump
`Versioning.ProtocolVersion` whenever the wire breaks; for now,
treat "everyone visible in /api/agents reports the same Version" as
the smoke test.

### Release workflow

`.github/workflows/release.yml` triggers on `v*.*.*` tag push:

1. `validate-version` (ubuntu) -- asserts tag matches `VERSION`.
2. `build` (windows-latest) -- self-contained `dotnet publish` of agent
   + launcher, install Inno Setup via `choco`, run `iscc.exe`, compute
   SHA256, generate `version.json`.
3. `release` (ubuntu) -- generate changelog from `git log`, create a
   **draft** GitHub release with `magpilot-setup-X.Y.Z.exe`,
   `magpilot-setup-X.Y.Z.exe.sha256`, and `version.json` as assets.

Releases are **draft** by default -- the user reviews + publishes
manually, the same way commits are reviewed before push.

### Ship checklist

When making a release:

1. Edit `VERSION` to the new semver.
2. Commit ("bump VERSION to X.Y.Z" is a fine message).
3. `git tag vX.Y.Z`, `git push --tags`.
4. Wait for the Action; review the draft release; publish it.
5. On the dev machine: `magpilot --magpilot-update` to test.

## Architectural rules and gotchas

These are the things that have already bitten us — DO NOT relearn
them by breaking them.

### ACP (Agent Client Protocol)

- `copilot --acp` speaks JSON-RPC 2.0 over stdio. We talk to it from
  `AcpSessionManager`. ACP spec: https://agentclientprotocol.com/.
- **`session/load` is NOT idempotent.** It throws JSON-RPC error
  `-32602 "Session is already loaded"` if you call it on a session
  that's currently loaded in this `copilot` process. Only call
  `session/load` for `Past` sessions or `LiveOrphan` sessions
  (after killing the foreign holder). For `LiveOwned` (already
  loaded by us), use the cached state and call `session/prompt`
  directly.
- **`session/prompt` blocks until the turn ends** (up to ~600s). The
  HTTP `/messages` endpoint must therefore be **fire-and-forget**:
  return 202 immediately, await the prompt in a background `Task`,
  and tell the SPA the turn ended via an SSE `TurnComplete`
  event. Never let the request thread sit on `await session/prompt`.
- **A second `session/prompt` while a turn is still running INTERRUPTS
  the first.** Copilot CLI cancels the in-flight turn (emitting an
  `Info: Operation cancelled by user` assistant chunk + a `turn_complete`
  for it), then runs a fresh turn that has both user messages in context.
  It does NOT queue. Every subscriber to the session's SSE stream sees
  that cancellation `turn_complete`, so any reply consumer that stops on
  the first `turn_complete` cuts the original reply short. Clients driving
  a shared/pinned session must serialize prompts themselves -- the SPA
  queues while a turn is in flight (Send -> Queue, FIFO drain on
  `TurnComplete`); any new ACP-driving client needs the same per-session
  in-flight guard.
- **ACP `sessionUpdate` kind values for tools are `tool_call` + `tool_call_update`, NOT `tool_call_start` / `tool_call_progress` / `tool_call_end`.** The latter (suffix variants) are what `Magpilot.Shared.Models.StreamEvent` calls them on the SSE wire, and they're the names the [ACP spec](https://agentclientprotocol.com/) uses in prose, but the actual JSON `sessionUpdate` field that Copilot CLI emits is the un-suffixed pair:
  * `"sessionUpdate":"tool_call"` with `"status":"pending"` -- a new tool call (built-in or MCP).
  * `"sessionUpdate":"tool_call_update"` with `"status":"in_progress"|"completed"|"failed"` -- subsequent updates.
  
  `AcpSessionManager.HandleUpdate` maps `tool_call -> ToolCallStart`, `tool_call_update -> ToolCallEnd` (for completed/failed) or `ToolCallProgress` (otherwise). **This silently broke for ~weeks before 2026-05-18**: the original switch was wired to the suffix variants, every tool_call was discarded as unknown, the SPA's live stream had no block delimiter for built-in tools (bash, etc.) so multi-step assistant responses concatenated into one bubble, and the WhatsApp sidecar's per-response flush trigger fired only on `thought_delta` (which doesn't always interleave). Fixed by mapping the correct kinds; if you ever see "live stream is one bubble but refresh shows N bubbles", suspect this regressed.
- ACP streams thoughts and assistant content as **separate** updates:
  `agent_message_chunk` -> `MessageDelta`, `agent_thought_chunk` ->
  `ThoughtDelta`. Both are mapped in `HandleUpdate`. The UI renders
  them differently (thought = blue-bordered `<details>`, message =
  Markdown).
- **`copilot --acp` does NOT auto-load plugins** the way the
  interactive CLI does. `AcpClient.BuildPluginDirArgs` reads
  `~/.copilot/settings.json`, picks `installedPlugins[]` entries with
  `enabled: true`, and appends `--plugin-dir <cache_path>` per plugin
  before spawning. If you find yourself wondering "why doesn't the
  agent's session see my marketplace skill?" -- check that this
  forwarder fired in the agent log on startup
  (`Forwarding N enabled plugin(s) to ACP child via --plugin-dir`).
- **Process.Start launcher-shim resolution.** On Windows an
  unqualified exe name (`"copilot.exe"`) goes through CreateProcess
  which can resolve to a launcher shim that re-execs and exits,
  leaving us with a broken stdio pipe. `AcpClient.ResolveOnPath`
  walks `PATH` ourselves and passes the fully-qualified path to
  `ProcessStartInfo`. Don't undo this; it's the same binary, just
  qualified.
- **`session/close` is NOT implemented** in the current copilot
  CLI's `--acp` mode -- it returns `-32601 "Method not found"`. So
  there is no clean way to evict a session from the multiplex
  child's in-memory map. `session/load` on an already-loaded
  session returns `"already loaded"` rather than re-reading disk.
  Practical consequence for `SessionRegistry.ReleaseFromHostAsync`:
  if the host has appended new events to `events.jsonl` while it
  drove the session, the agent's multiplex copy is **stale** -- it
  still holds whatever it knew at acquire-for-host time. Documented
  as a known limit; the proper Phase 2+ fix is killing+respawning
  the default-flavor multiplex child on each release.
- **Dead-child respawn in `AcpFlavorPool.AcquireAsync`.** A long-running
  agent eventually sees its cached `copilot --acp` child die (crash,
  OOM, machine sleep, copilot CLI self-update mid-run). Without
  detection, every later `CallAsync` writes into the broken stdin
  pipe and waits the full 120s `WaitWithTimeoutAsync`. Symptom: new
  sessions hang ~30s client-side, then surface
  `System.TimeoutException: ACP call id=N timed out`, and Preflight
  context discovery cascades (it routes through `quick-prompt` ->
  `session/new`). Fix in place: `AcpClient.IsAlive` (`!_proc.HasExited`)
  + `FailAllPending` in the read loop's `finally`. The pool's
  `AcquireAsync` checks `IsAlive` before returning a cached client
  and respawns if dead, logged at Warning level (visible in
  `/admin/logs`). **Do not undo this; the underlying root cause is
  unfixable from our side.**
- **`AcpSessionManager._inFlight`** tracks active `PromptAsync` calls
  keyed by sessionId, with the requester label and start time. Used
  by `GET /sessions/{id}/state` to report activity without polling,
  and by `WaitForTurnBoundaryAsync` (the polite acquire-for-host
  path) so the agent hands off at a clean turn boundary instead of
  mid-stream. The `requester` parameter on `PromptAsync` is what
  surfaces as `inFlight.driver` in the take-over prompt -- pass a
  meaningful label whenever you call it from a new code path.

### Hub -> agent HTTP timeouts (three named clients, don't merge them)

The hub talks to each agent over plain HTTP and the timeout budget
is **deliberately split into three named `HttpClient`s** in
`Magpilot.Hub.Program.cs`. Picking the wrong one causes either
silent agent flakiness or stalled SPA aggregation -- both have
already burned us in production.

| Client name      | Default timeout | Tunable                        | Used for                                                                 |
|------------------|-----------------|--------------------------------|--------------------------------------------------------------------------|
| `agent`          | 10s             | `Hub:AgentHttpTimeoutSec`      | Fast read-only control-plane (GET `/api/sessions`, `/api/info`, etc.)    |
| `agent-action`   | 90s             | `Hub:AgentActionTimeoutSec`    | Mutating ACP-driving calls (`POST /api/sessions`, `/sessions/{id}/adopt`) |
| `agent-stream`   | infinite        | (n/a)                          | SSE proxy and `/quick-prompt` (turns can run minutes)                    |

Pick via the `AgentClientKind` enum on `AgentHttpClient.ClientFor(name, kind)`:
`Read` (default), `Action`, or `Stream`.

**Why three not one:** `agent` has to fail fast (10s) so a single dead
agent can't stall the SPA's host list. But `POST /api/sessions` on
the agent calls ACP `session/new` which routinely takes 5-30s under
plugin load -- if it shares the 10s budget, the hub raises
`TaskCanceledException`, **catches it as "agent unreachable", marks
the agent OFFLINE, and returns 502 to the SPA** even though the
session is being created successfully on the agent. Symptom: SPA
"new session" toast shows 502, agent disappears from the host list
until the next discovery probe. If you see this regress, check
that the relevant endpoint in `HubEndpoints.cs` still calls
`http.ClientFor(name, AgentClientKind.Action)`.

**Don't add a new mutating endpoint without thinking about which
client it should use.** `/messages` is fire-and-forget on the agent
(returns 202 immediately), so `Read` is fine. `/detach`,
`/interrupt`, `/approvals/{id}` are all quick -- `Read`. Anything
that triggers `session/new`, `session/load`, or other ACP work
that can stall: `Action`. Anything that holds an open response
body for a turn: `Stream`.

### Pinned sessions (long-lived) and `/quick-prompt`

Beyond the ephemeral sessions the SPA creates, Magpilot supports
**pinned sessions**: stable IDs that survive agent restart by being
adopted-on-demand. They're how external clients (cron, WhatsApp,
anything talking to the agent's HTTP API) can route into a long-lived
conversation instead of spawning a throwaway one.

- `POST /api/sessions` accepts an optional `name` to create one.
- `POST /api/quick-prompt` accepts an optional `sessionId` -- when
  provided, the agent adopts-on-demand and routes the prompt to that
  session. When omitted, it creates an ephemeral session, runs the
  prompt, and detaches.
- **`POST /api/sessions/{id}/messages` does NOT adopt-on-demand** -- only
  `/quick-prompt` does. `/messages` assumes the session is already loaded
  and drives `session/prompt` directly; against a dormant (unloaded)
  pinned session the prompt fails silently (turn ends `stopReason="error"`,
  no assistant text). Clients that POST `/messages` at a pinned session
  (the SPA's live path, sidecars) rely on it being loaded already -- the
  SPA adopts-on-open, and a deployment's bootstrap should adopt each pinned
  session on restart. Leaving one dormant after an agent restart is the
  classic "talked to it after a power-cut and got no reply" failure.
- ACP only emits `user_message_chunk` during `session/load` history
  replay -- never for live prompts. So `/quick-prompt` against a
  pinned sessionId synthesizes a `UserDelta` into the broadcast
  channel via `acp.PublishToSubscribers(...)`. Without this, an SPA
  tab subscribed to the same pinned session would see the assistant
  reply but no question.

### SPA stream lifecycle (`Home.razor`)

- **Don't undo the defensive cancel in `StartStreamingAsync`.** A
  race between two `OnParametersSetAsync` invocations (cascading
  parameter change while a previous lifecycle await was in flight)
  can otherwise spawn a second `Task.Run` pumping events into the
  same `Apply()` -- chunks land twice, the streaming bubble shows
  duplicated/interleaved characters, and refresh is the only fix.
  The existing code: cancels any pre-existing `_streamCts`, claims
  `_streamingFor` synchronously at the top of the routing branch,
  and uses a `_streamGeneration` counter so any stale pump bails on
  the next iteration.

- **SSE auto-reconnect on backgrounded mobile tabs.** Android
  Chrome (and iOS Safari) tear down the underlying SSE socket when
  the user backgrounds the tab. On resume, the still-pumping
  `await reader.ReadLineAsync(ct)` inside
  `Magpilot.UI.Services.HubClient.StreamAsync` would otherwise
  surface as a `BrowserHttpInterop` `HttpRequestException`
  (`TypeError: network error`) and bubble up as an unhandled
  exception, leaving the chat permanently frozen and spamming the
  central log. Two-part fix in place:
  1. `HubClient.StreamAsync` catches `HttpRequestException` /
     `IOException` around the `ReadLineAsync` call and treats them
     as a clean end-of-stream (`yield break`) instead of throwing.
     Don't replace this with a re-throw -- the upstream connection
     will be re-established by Home.razor.
  2. `Home.razor`'s pump task, when the foreach exits naturally
     (or via the silent yield-break above) AND the user is still on
     the same session AND the CT isn't cancelled, transparently
     restarts the stream with `load: false` and a snapshot of
     `_messages` as `restoreFrom`. Backoff is exponential capped at
     10s, with `_streamReconnectAttempts` resetting on any received
     event. `Task.Delay` is throttled while the tab is hidden, so a
     backgrounded phone naturally waits to reconnect until you
     foreground it -- no battery-hungry retry storm.

  **Important:** the `isReconnect` parameter on
  `StartStreamingAsync` exists so a self-reconnect doesn't reset
  the attempt counter -- a real navigation always passes
  `isReconnect: false` (the default). Don't drop this distinction.

### Session classification (`SessionScanner`) and ownership

A copilot session on disk has an `inuse.<PID>.lock` file when held by
a live process. We classify into:

- **Owned** -- lock PID is one we (this agent) spawned. We own it.
- **Locked** -- lock PID is alive but is some other copilot
  process (e.g. a terminal session the user opened). To adopt, we
  must `Stop-Process -Id <PID>` it then `session/load` ourselves.
- **Dormant** -- no lock, or the lock PID is dead. Free to `session/load`.

(Wire format is the integer ordinal of `SessionState`. Order matters:
Owned=0, Locked=1, Dormant=2.)

> **`inuse.<PID>.lock` is NOT a real mutex.** Empirically verified
> on 2026-05-13: an interactive `copilot --resume` happily added an
> `inuse.226288.lock` alongside an existing `inuse.44380.lock` from
> our multiplex `copilot --acp`. Two PIDs claim the same session
> simultaneously and the file system does nothing to prevent it.
> The original "Known leak" note (a live `--resume` not refreshing
> a lock at all) is a stronger version of the same fact.
>
> Two follow-on quirks the launcher's `PostSpawnDetector` has to
> tolerate, both confirmed empirically:
>
> - **A fresh `copilot --resume=<sid>` of a session that already has
>   events writes a NEW `inuse.<new-pid>.lock` alongside the stale
>   one** (the multi-lock scenario above). The detector's primary
>   path is exact PID match on that new lock -- works for fresh
>   sessions, picker selections, --continue, and most resumes.
>
> - **A `copilot --resume=<sid>` of a session that has NOT yet
>   accumulated events does NOT refresh the lock at all** -- the
>   stale `inuse.<dead-pid>.lock` is the only one on disk for the
>   entire run. The detector's stranded-resume fallback (Pass 2
>   in `PostSpawnDetector.WaitForSessionAsync`) covers this: a
>   session whose lock holder PID is dead AND whose `events.jsonl`
>   or `workspace.yaml` mtime advances past the snapshot taken at
>   spawn time is the resume target. The dead-lock condition is
>   re-validated each tick so a session that picks up a live lock
>   mid-detection (handled by Pass 1) never also fires Pass 2.
>
> The launcher avoids both quirks entirely whenever the user gave
> it a UUID up front -- `WrapperOptions.ExtractKnownSessionId`
> recognizes UUID values from either `--resume=<UUID>` or
> `--session-id=<UUID>` and `Program.cs` routes straight to
> `RunSessionLoopAsync` with that sid instead of falling into
> post-spawn detection.
>
> **Authoritative ownership lives in agent memory, NOT in the
> filesystem.** `Sessions/HostOwnership.cs` keeps the canonical map
> of `sessionId -> hostPid` for sessions a `magpilot` wrapper
> currently drives. All ACP-driving endpoints (`/messages`,
> `/interrupt`, `/approvals/{id}`) consult it and return 409
> Conflict + `HostOwnedResponse { needsRelease, hostPid }` when
> host-owned. The wire view (`GET /state`) reports
> `owner: "Host"` from this map, NOT from the lock files.
>
> A 10s background sweep prunes `HostOwnership` entries whose holder
> PID is no longer alive, so a wrapper that crashed or was kill -9'd
> doesn't leave the session permanently stuck.

`UpdatedAt` is derived from the latest mtime between `events.jsonl`
and `workspace.yaml`. **Do NOT** trust the `updated_at` field inside
`workspace.yaml` -- it isn't rewritten on every message. The
`/api/sessions` endpoint sorts DESC by `UpdatedAt`.

`SessionScanner` has a tiny line-based YAML parser that **unfolds
`|-` block scalars** when reading `name`/`summary`. Without that, a
pinned session whose name starts with a quote or contains a newline
renders in the SPA list with the literal `|-` as its title.

> **Bonus empirical: events.jsonl is a multi-writer DAG, not a log.**
> Reproduced on 2026-05-12 with VS Code's "connect" feature: VS
> Code's extension host writes a `session.resume` event then drives
> the session via its own model-API path, appending events as if it
> were the CLI. The CLI does NOT tail its own `events.jsonl`, so
> when both sides write, parentId chains fork. Each event has a
> `parentId`; the file linearizes by time but the logical structure
> is a DAG. `HostOwnership` is the magpilot-side enforcement that
> we never end up in this state ourselves.

### SPA session-state contract (`Home.razor`)

The SPA keeps a per-session message cache (`_msgCache:
Dictionary<sessionKey, List<ChatMessage>>`). On switch, three branches:

- **Owned + cache present**: restore from cache, call
  `AdoptAsync(load: false)`. **Never** call `session/load` here.
- **Owned + no cache** (e.g. fresh tab opening a pinned session that
  another client has already loaded into ACP): we can't `session/load`
  (ACP rejects double-load) and we have no in-tab cache. Fall back
  to `GET /api/agents/{name}/sessions/{id}/history` -- backed by
  `HistoryReader` reading `events.jsonl` directly and projecting to
  flat role/text rows -- then connect the live stream with
  `load: false`.
- **Dormant or Locked**: call `AdoptAsync(load: true)` to replay
  history; that history is then authoritative.

The SPA is the only producer of new visible messages while the user
is connected, so the cache is safe as last-word for `Owned`.
When the agent restarts, *all* sessions become Dormant/Locked and
get a fresh `session/load` on next visit.

### SPA pitfall: there is NO bare `HttpClient` in the WASM container

`Magpilot.Web/Program.cs` registers `HubClient` and `HubLogClient`
as singletons, each constructing its OWN `HttpClient` wrapped in
`IncludeCredentialsHandler` so the auth cookie flows. There is NO
plain `HttpClient` in the DI container. Any new page that
`@inject HttpClient Http` will throw at component instantiation:

```
Cannot provide a value for property 'Http' on type '<page>'.
There is no registered service of type 'System.Net.Http.HttpClient'.
```

Always go through `HubClient` (or extend it). This bit `Logs.razor`
on 2026-05-11 -- it injected bare `HttpClient` and exploded on
every navigation to `/admin/logs` until rerouted.

### SPA pitfall: `Console.Error.WriteLine` shows the yellow error banner

The Blazor WebAssembly runtime config wires `Console.Error` (C# stderr)
through its `dotNetCriticalError` handler, which does
`console.error(msg) + showBlazorErrorUi()`. Any C# code that calls
`Console.Error.WriteLine(...)` -- even just to log a recoverable
"hub-flush failed once" -- pops the yellow "An unhandled error has
occurred. Reload." banner over the entire page.

This bit us hard on mobile on 2026-06-15: every time `HubLogClient`'s
drain task hit a transient `TypeError: Failed to fetch` after a tab
resumed from being backgrounded, `Console.Error.WriteLine` made the
banner appear, the user reloaded, and the experience felt like the
app crashed -- when in fact the SSE auto-reconnect had already
recovered. Same pitfall for any other recoverable-error-with-a-
diagnostic-log site (catch + report + retry, etc.).

Rule:

- **In Magpilot.Web / Magpilot.UI: NEVER use `Console.Error.WriteLine`
  unless you genuinely want to terminate the SPA with the yellow
  banner.** For diagnostic output, use `Console.WriteLine` (stdout
  goes to F12 console at info level without triggering the banner)
  or, better, `ILogger<T>` (registered SPA-side provider routes to
  `/admin/logs` plus the F12 console at the runtime-mutable
  verbosity level).
- The recovery-path callers in `HubLogClient` + `Logs.razor` use
  `Console.WriteLine` deliberately -- they can't go through `HubLog`
  or `ILogger` without risking a feedback loop (those failures are
  what they're reporting).
- The JS-side belt-and-braces shim in `Magpilot.Web/wwwroot/js/error-capture.js`
  still installs a `MutationObserver` on `#blazor-error-ui` and
  `sendBeacon`s the captured text to `/api/log` with source
  `spa-fatal` if the banner ever does show, so a genuine future
  unhandled exception still surfaces -- but the bar shouldn't be
  triggered by app code in the first place.

### Cooperative single-owner handoff (magpilot launcher)

The shim project. A `magpilot` wrapper (in `src/Magpilot.Host`)
PATH-aliased as `copilot` coordinates with the agent so a session is
driven by exactly one process at any moment. Web/SPA + WhatsApp
preempt cooperatively at clean turn boundaries instead of the agent
killing PIDs and hoping. Full design + log:
`copilot-context/ideas/projects/magpilot-shim.md`.

Wire contract this code base now exposes:

- **`GET /api/sessions/{id}/state`** -- read-only. Returns
  `SessionStateInfo { info, owner: "None"|"Agent"|"Host"|"External",
  hostPid?, activity: "Idle"|"InFlight"|"JustFinished",
  inFlight?: { driver, startedAtMs, preview }, lastEvent?: { type,
  id, timestamp } }`. Cheap. Wrapper calls on every startup; SPA
  also calls it from `Home.razor.OnParametersSetAsync` to detect a
  pre-existing host-owned session before deciding to stream.
- **`POST /api/sessions/{id}/release-request`** -- body
  `ReleaseRequestBody { Requester, Force }`. Broadcasts a
  `ReleaseRequested` SSE event on the session's stream so any
  subscribed wrapper can begin its graceful shutdown. Idempotent.
  Both the launcher (before acquiring) and the SPA (before a
  forceful take-back from the host) fire this so the OTHER side
  has a chance to react before ownership flips.
- **`POST /api/sessions/{id}/acquire-for-host`** -- body
  `AcquireForHostBody { HostPid, Force }`. Atomic combined op:
  if we own the session and a turn's in flight, polite waits via
  `WaitForTurnBoundaryAsync` (force issues ACP cancel + 2s grace).
  Then `DetachAsync` + `HostOwnership.Set`. Returns refreshed state.
- **`POST /api/sessions/{id}/release`** -- body `ReleaseFromHostBody
  { HostPid }`. 409 Conflict if the wrong host PID claims to release.
  On success: clears `HostOwnership`, attempts `session/load` (tolerates
  "already loaded" -- see ACP gotchas).
- **SSE `release_requested` event** added to `StreamEvent` discriminator.

ACP-driving endpoints (`/messages`, `/interrupt`, `/approvals/{id}`)
return **`409 Conflict`** with body `HostOwnedResponse { Error,
NeedsRelease, HostPid }` when `HostOwnership` shows host-owned. The
SPA's `HubClient.SendPromptAsync` and the WhatsApp sidecar's
`postPromptWithReleaseKnock` both handle the 409: fire
release-request, poll state for up to 60s, retry the POST. On final
timeout the SPA throws `HostStillOwnedException` (caught by
`Home.razor` -> "Take over from terminal" `MudAlert`); the WhatsApp
sidecar sends a permanent failure note to chat.

`Hub` proxies the four shim endpoints via the existing `Forward(resp)`
pass-through helper. The 409 propagates with the right shape because
`Forward` preserves status + body verbatim.

When you add a new caller of `/messages` (or anything that drives
ACP), wrap it with the same retry-on-409 pattern. Don't re-implement
ad hoc.

**Pre-acquire release-request** (launcher side, `Magpilot.Host/Program.cs`):
both spawn paths -- known-sid (`--resume=<UUID>`) and post-spawn
detection (no sid, picker, --continue) -- fire
`agent.FireReleaseRequestAsync` + a 500ms grace BEFORE
`AcquireForHostAsync`. Without this courtesy the SPA only learns
ownership flipped when its NEXT `/messages` POST returns 409 (i.e.
the user has to send before the UI updates). Failure of the broadcast
is non-fatal; the existing 409 path still catches uncoordinated cases.

**SPA-side reactivity** (`Magpilot.UI/Pages/Home.razor`):
- `Apply()` handles the `ReleaseRequested` SSE case: sets
  `_hostHasTakenOver`, drops the in-progress streaming bubble +
  thinking debounce, and stops the SSE pump. The chat-pane render
  branch shows a MudAlert "A terminal session is now driving this
  conversation -- composer paused" with a "Take back" button. The
  composer's `Disabled` binding folds in the flag so input is locked
  while host-owned.
- `OnParametersSetAsync` calls `Hub.GetStateAsync` after the
  session-list fetch and BEFORE deciding what to stream. If
  `state.Owner == SessionOwner.Host`, the same takeover state is
  set and the stream is skipped entirely -- the SPA never opens an
  SSE pump for a session it's not allowed to drive.
- `HandleTakeBackFromHost` is the symmetric counter-flow:
  `FireReleaseRequestAsync(force=true)` + 1s grace +
  `AcquireForHostAsync(0, force=true)` + `ReleaseAsync(0)` + restart
  stream from cache (or `/history` if no cache). The polite knock
  gives a cooperative launcher time to tear down its PTY cleanly;
  the forceful flip catches stuck cases.

**Agent-side stale-lock cleanup** (`Magpilot.Agent/Acp/AcpSessionManager.cs`):
`CloseAsync` takes a `string? sessionsRoot` parameter and, after
the `session/close` call (which copilot --acp rejects with -32601
today), deletes the agent's
`<sessionsRoot>/<sid>/inuse.<acp-pid>.lock` file. The on-disk lock
is what OTHER copilot processes (a launcher's interactive child,
terminal-driven `copilot --resume`, etc.) consult to decide
whether the session is "in use". Without this cleanup, every
launcher startup against a session the agent loaded printed
"session is already in use by another process" and the new copilot
piled its own lock on top (multi-lock state). `AcpClient.ProcessId`
exposes `_proc?.Id` so the cleanup code knows which lock filename
to target.

### The "input never re-enables" pitfall

Anywhere you wait for a turn to end, you MUST observe the SSE
`TurnComplete` event. Don't tie input enablement to the HTTP
response of `/messages` — that returns 202 immediately.

## Central logging

We have an in-hub log sink so SPA crashes (and agent warnings) leave
a debuggable trail without the user having to open browser devtools.

- **Storage**: `LogStore` writes to `MAGPILOT_HUB_DATA/logs.db`
  (SQLite + FTS5). Capped at 50,000 rows; a background trim runs
  every 5 minutes so disk stays bounded.
- **Endpoints**:
  - `POST /api/log` and `POST /api/log/batch` -- ingest. Cookie auth
    works (the SPA gets it for free); for non-cookie clients use
    `MAGPILOT_HUB_BEARER`.
  - `GET /api/log` -- query (`source`, `level`, `search`, `sessionId`,
    `since`, `limit`). FTS used when `search` is supplied.
  - `GET /api/log/sources` -- list of known origins (drives the
    viewer's source dropdown).
- **Producers**:
  - SPA: `wwwroot/js/error-capture.js` hooks `window.onerror` and
    `unhandledrejection` and forwards through `JsErrorBridge`
    (static `[JSInvokable]`) into `HubLogClient`. Several
    `Home.razor` try/catch sites also call `HubLog.LogError(...)`
    explicitly with `sessionId` + `agent` in extras.
  - SPA `ILogger<T>`: `Magpilot.UI.Logging.HubLoggerProvider` is
    registered as an `ILoggerProvider`, so any component that
    `@inject ILogger<Foo>` and calls `LogTrace/LogDebug/...` flows
    through the same hub sink. Filtered by
    `Magpilot.UI.Logging.LogLevelGate.MinLevel` -- the gate is
    runtime-mutable from the verbosity dropdown on `/admin/logs` or
    the `?logLevel=Trace` query param, with localStorage
    persistence (`magpilot.logLevel`). The framework filter only
    runs the gate against `Magpilot.*` categories; framework
    categories (`Microsoft.AspNetCore.*`, `System.*`) stay pinned
    to `Information` so a Trace toggle doesn't unleash render-tree
    debug noise (~10k events per turn).
    **Use `_logger.LogTrace` (with structured templates) for any
    diagnostic breadcrumbs in SPA components instead of
    `Console.WriteLine`** -- the latter bypasses the gate, can't be
    toggled remotely, and never reaches `/admin/logs`.
  - Agents: `Magpilot.Agent.Logging.HubLoggerProvider` is registered
    in `Program.cs` as an `ILoggerProvider`. It filters
    `LogLevel.Warning+` and POSTs batches every ~2s. No-op when
    `MAGPILOT_HUB_URL` or `MAGPILOT_HUB_BEARER` aren't set, so dev
    runs without a hub still work.
  - Sidecars: TODO (see `central-logging-sidecars` backlog item).
- **Source labels** (convention): `spa`, `hub`, agent name (e.g.
  `magnus`, `HENDRIK`), sidecar name (e.g. `whatsapp`, `cron`).
- **Viewer**: `/admin/logs` (linked from the AppBar). MudBlazor with
  source/level/text/session filters, time-window picker, drill-down
  per row for stack/extra/UA, and a 5s auto-refresh toggle.
- **Drop policy**: bounded channels with `DropOldest` on overflow on
  both client + agent so a flaky hub can't OOM the producer or
  feedback-loop into the log pipeline.

**Workflow**: when something crashes, refresh the SPA and open
`/admin/logs` filtered to `level=Error, last 15 min`. The bug is
usually already there with stack + URL + UA.

## NPM + reverse proxy

The hub sits behind NPM (also on LXC 102). Critical SSE settings live
in the proxy host's Advanced field:

```nginx
proxy_buffering off;
proxy_cache off;
gzip off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

If you ever see SSE events arrive in giant batches instead of
streaming, NPM buffering has been re-enabled. Fix: above advanced
config; do NOT enable Caching in the NPM UI.

## SPA / brand

- **Design system**: MudBlazor 9.4. The theme lives in
  `Magpilot.UI/MagpilotTheme.cs` (deep midnight blue light + dark
  palettes, Inter typography). `MainLayout` cascades a `ThemeState`
  record so any descendant in `Magpilot.UI` can flip dark mode
  without circular references between the Web project and the RCL.
  Choice persists to `localStorage["magpilot.darkMode"]`.
- **JS lives in `Magpilot.Web/wwwroot/js/`**, NOT collocated
  `*.razor.js` in the RCL. The hub Dockerfile's multi-stage publish
  trips `BLAZOR106` on collocated `_content/...` assets at the
  second publish step. Don't move it.
- **Mobile viewport**: use `100dvh`, never `100vh` (mobile chrome
  counts toward `vh`, so the composer floats off the bottom). The
  meta tag uses `interactive-widget=resizes-content` so the soft
  keyboard pushes content up instead of overlaying. `MudMainContent`
  already insets the AppBar; don't double-subtract its height.
- **Brand mark**: `Magpilot.UI/wwwroot/favicon-mark.svg` is the
  bg-stripped magpie. Lives in the RCL so satellite SPAs (e.g.
  Magnus.Web) reference it as `_content/Magpilot.UI/favicon-mark.svg`.
  `Magpilot.UI/Components/MagpieMark.razor` is the single source of
  truth for rendering it inside C# components (params: `Size`,
  `Glow`, `Class`). Don't re-add a local copy of the SVG to any
  consumer; the one in the RCL is canonical.
- **Brand-themed loader**: both SPAs (`Magpilot.Web/wwwroot/index.html`
  + `Magnus.Web/wwwroot/index.html`) use a custom loading screen
  with the magpie mark + a teal arc driven by
  `--blazor-load-percentage`. The CSS lives in each app's
  `wwwroot/css/app.css`. Don't re-introduce the default Blazor
  circle SVG.
- **Two-pane drawer in `Home.razor`**: the host-list and session-list
  are mutually exclusive panes within the drawer (not stacked
  sections). Pick a host -> sessions pane with a back-arrow header +
  the host's name + the New chat button. Back arrow flips
  `_showHostsPane` without touching the URL or the open chat. Don't
  merge them back into one tall list. Re-clicking the active host in
  the host pane closes the pane (handled via `OnAgentClicked` instead
  of MudList's `SelectedValueChanged`, which would no-op a same-host
  click) -- so users who bounced into the hosts pane just to glance
  around aren't trapped behind it.
- **HostName component** (`Magpilot.UI/Components/HostName.razor`):
  single source of truth for "host name + status indicator" pairs.
  Two render modes:
  * AppBar: `Outlined=true`, `TextTypo=Typo.h6`, with `Color` +
    `StatusText` parameters bound to the SSE stream state
    (`StreamStatus.Connected/Reconnecting/Offline`) so the pill's
    border tracks live connection state. Always visible (not
    breakpoint-gated -- the pill IS the live status surface). Spinner
    swaps in for the dot when `Indeterminate=true` (reconnecting);
    a CloudOff glyph swaps in when the colour is `Color.Error`.
  * Drawer / inline: plain inline `<div>` -- no border, no live
    status (the AppBar pill already covers that). Used in the
    sessions-pane back-arrow header for orientation only.
  Don't double-encode status: drawer header doesn't render a dot.
- **Clipping a MudChip's text** (or any MudBlazor component that
  renders content into an inner slot) needs three things together
  -- any one missing and the chip stays wider than the parent
  flexbox can accommodate:
  1. The selector must reach the chip from a sheet that's actually
     loaded for it. Razor's `::deep` scoping silently does NOT
     propagate into `MudListItem`'s rendered subtree (and probably
     other MudBlazor wrappers too), so a `::deep .my-chip` rule from
     a page's scoped `.razor.css` will fail to match when the chip
     lives inside MudListItem. Put the rule in
     `Magpilot.Web/wwwroot/css/app.css` (global) and select the
     class name directly -- no `::deep`.
  2. `min-width: 0` on BOTH the chip root AND `.mud-chip-content`
     -- flex items default to `min-width: auto` which means
     "don't shrink below content", and the chip's own intrinsic
     width otherwise wins over any `max-width` you set on the
     parent.
  3. `overflow: hidden` + `text-overflow: ellipsis` +
     `white-space: nowrap` + `display: block` on `.mud-chip-content`
     so the truncated text gets the ellipsis treatment.
  Wrap the chip in a `MudTooltip` so hover restores the full text.
  The session-list rendered in `Home.razor` deliberately AVOIDS chips
  for repo + branch -- it uses plain `MudText` rows with native
  `title=` tooltips because the alignment + truncation contract is
  cleaner without MudChip's intrinsic-width fight. If you re-add a
  chip somewhere, re-derive the three rules above.
- **Owner-prefix stripping** for `SessionInfo.Repository`: the
  Copilot CLI writes the repo name as `owner/repo` in
  `workspace.yaml`. If every session you ever open is your own
  repo, every chip starts with `<your-handle>/`. The SPA fetches
  `GET /api/me` on init and `Home.razor`'s `DisplayRepo()` helper
  strips the `{identity}/` prefix from chip TEXT when it matches
  the signed-in user. Repos from other owners render unchanged.
  The chip's tooltip always renders the full label so the owner
  is one hover away. **Don't hard-code a specific username
  anywhere** -- this is identity-driven precisely because magpilot
  is generic and someone else's deployment will have a different
  signed-in user.

## Operational gotchas

- **Windows agents used to leak the ACP child** if the parent died
  abnormally (taskmgr kill, Stop-Process on the dotnet PID without
  first killing the child). The orphan kept its sessions hot and
  the next agent run saw them as Dormant-but-actually-live. **Fixed
  in v0.1.7** by enrolling every spawned `copilot --acp` child in a
  Win32 Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
  (`Magpilot.Agent.Acp.Win32JobObject`). When the agent process
  dies for any reason, Windows closes the last handle to the job and
  atomically kills every member process -- including grandchildren
  the copilot child spawned (bash subprocesses, MCP servers, etc).
  On Linux the helper is `OperatingSystem.IsWindows()`-gated to a
  no-op; POSIX does NOT auto-kill orphans (they reparent to init).
  Magnus is fine in practice because it runs in a Docker container
  whose PID namespace gets killed by the kernel when the container
  exits, and Compose's `restart: unless-stopped` spawns a fresh
  container with a fresh PID namespace. **For a hypothetical
  bare-metal Linux deployment, the orphan bug would re-appear** --
  see the `agent-linux-orphan-protection` item in "What is NOT yet
  built" below.
  If you ever see an orphaned `copilot.exe` outliving its parent
  agent on Windows, suspect a Job-Object regression.
- **Bootstrap of pinned sessions** should remove stale
  `inuse.<PID>.lock` files before adopting; otherwise the scanner's
  paranoia (see "Known leak" above) fires and we needlessly
  classify our own freshly-spawned session as `Locked`.

## Style and conventions

- Target framework: `net9.0` for everything. C# language version: latest.
- File-scoped namespaces, primary constructors, pattern matching, `var`
  when type is obvious, collection expressions, raw string literals.
- xUnit for tests (none yet — when you add them, follow AAA and use
  `Fact`/`Theory`).
- ASCII only in source comments and string literals (PowerShell-side
  encoding can mangle non-ASCII characters in commit pipelines).
- Keep DTOs in `Magpilot.Shared`. The SSE wire format
  (`StreamEvent` discriminated union) is the contract between agent
  and SPA — bumping it requires both sides to ship together.

## Useful operational commands

```pwsh
# Tail the hub logs on the LXC (process logs, NOT the central /api/log sink)
ssh proxmox "pct exec 102 -- docker logs --tail 200 -f magpilot-hub"

# Restart the agent on HENDRIK (in the existing 'agent' async session)
#  (in the agent shell: Ctrl+C, then `dotnet run --project src/Magpilot.Agent`)

# Ship a freshly-built image to the LXC.
#
# Routine releases: NOTHING TO DO HERE -- the GitHub Action publishes
# ghcr.io/chsienki/magpilot-hub on tag push and watchtower on the LXC
# auto-pulls within 5 min. To force the pull immediately:
ssh proxmox "pct exec 102 -- bash -lc 'cd /srv/magpilot && docker compose pull hub && docker compose up -d hub'"

# Emergency local build (CI down, unreviewed patch): the old
# save-load flow still works, just tag for ghcr so compose finds it.
docker save ghcr.io/chsienki/magpilot-hub:emergency-$(Get-Date -Format yyyyMMdd-HHmm) -o magpilot-hub.tar
scp magpilot-hub.tar proxmox:/tmp/magpilot-hub.tar
ssh proxmox "pct push 102 /tmp/magpilot-hub.tar /tmp/magpilot-hub.tar && \
             pct exec 102 -- docker load -i /tmp/magpilot-hub.tar && \
             pct exec 102 -- bash -lc 'cd /srv/magpilot && docker compose up -d --force-recreate hub'"

# First stop for any user-reported "magpilot crashed" report
# Open https://magpilot.home.sienkiewi.cz/admin/logs?level=Error&since=...
# in the browser. Stack + URL + UA are usually already there.

# Check NPM proxy host config for magpilot
$tok = (Invoke-RestMethod -Uri http://192.168.1.239:81/api/tokens -Method Post `
        -ContentType application/json `
        -Body (@{identity="<email>"; secret="<pwd>"}|ConvertTo-Json)).token
Invoke-RestMethod -Uri http://192.168.1.239:81/api/nginx/proxy-hosts/7 `
                  -Headers @{Authorization="Bearer $tok"}
```

## What is NOT yet built (don't assume it exists)

- The `CopilotChat.Maui` Android shell -- no project yet, but the
  shared `Magpilot.UI` is designed to drop into a MAUI Blazor Hybrid
  WebView later.
- Real FCM push and Web Push (VAPID) delivery.
- TLS between hub and per-host agents (currently plaintext over LAN +
  bearer token).
- Approval-prompt modals for risky tool calls.
- Sidecar-side log forwarders (the hub sink + agent forwarder are
  in; sidecars are still TODO).
- magpilot launcher extras: `--magpilot-status` only prints
  reachability so far (full session listing TBD); no-args picker
  (list sessions for cwd) + `--continue` not implemented; wrapper
  doesn't post to `/api/log` audit trail yet.
- Pattern gamma "fake IDE" (agent advertises its own
  `~/.copilot/ide/<sid>.lock` so the unmodified copilot binary
  auto-attaches and the agent intercepts the 6-tool MCP-over-pipe
  callback surface). Designed in the shim doc; deferred until SPA
  diff-review becomes a recurring ask.
- **agent-linux-orphan-protection** -- the Windows Job Object fix
  shipped in v0.1.7 has no Linux equivalent. Magnus survives today
  because Docker's PID-namespace cleanup atomically reaps everything
  in the container when the agent dies, so the orphan can't outlive
  the container. A bare-metal Linux deployment (no container) would
  hit the same orphan problem the Job Object solves on Windows.
  Likely fix: `prctl(PR_SET_PDEATHSIG, SIGTERM)` -- but that call
  has to be made by the CHILD, not the parent, and posix_spawn
  doesn't let us inject a callback between fork and exec. So this
  needs either (a) a tiny native preload shim that prctls then
  execs `copilot`, (b) a `LD_PRELOAD` library that hooks into the
  child's startup, or (c) cgroups v2 + cgroup.kill in a unit file.
  None of these matter as long as we only ship on Windows + Magnus's
  Docker container; raise priority when adding a non-container
  Linux deployment.

If you implement any of these, update `docs/plan.md` and this file.

## When in doubt

1. Read `docs/architecture.md` first for topology + the agent HTTP
   contract; `docs/plan.md` for the longer-form design rationale.
2. Deployment-specific context (the Magnus LXC layout, the WhatsApp
   sidecar's Baileys quirks, the home network) lives in a separate
   repo at `chsienki/copilot-context` (not on the cloud agent --
   only the user's local machine).
3. Don't commit without explicit user permission. The user prefers
   to review changes before they hit `main`.

## Sibling docs (keep in sync)

When you change anything in this file that describes the architecture,
the wire contract, the project layout, or the SPA conventions, **also
update the sibling docs in the same commit batch**. Their roles:

| Path | Role | Update when... |
|---|---|---|
| `.github/copilot-instructions.md` (this file) | Orientation for AI agents working on the repo. Covers gotchas, pitfalls, and "do NOT relearn this by breaking it" notes. | Anything in this repo's *behaviour* changes (new endpoint, new pattern, new gotcha discovered, project layout shifts). |
| `docs/architecture.md` | The public contract: the agent HTTP API table, the multi-client coordination story, the UI/SPA conventions. Has the wire-format truth in human-readable form. | Any new agent endpoint, any change to the 409 / SSE / multi-client coordination story, any new UI convention worth advertising. |
| `docs/plan.md` | The original design rationale ("v5: Blazor Hybrid + Web"). Reads as a pitch document. Largely historical now -- shows the intent at the time of design. | Major architectural pivots only (a new top-level component, dropping a planned feature). The day-to-day churn lives in this file + architecture.md. |
| `README.md` | Public-facing pitch + status + repository layout. Aimed at a human visitor to GitHub. | Status section changes (new components shipped, new headline capabilities); the repository layout block when projects are added/removed. |
| `deploy/README.md` | Operational recipe for shipping the hub image to LXC 102. | Anything that changes the build/save/scp/load/recreate steps. |
| (in magstronaut, NOT this repo) `magnus/README.md`, `whatsapp/README.md`, `cron/README.md`, `preflight/README.md` | Site-specific deployment recipes for the satellites. | Only update from inside the magstronaut repo when deploy plumbing changes. |

> **Rule**: any non-trivial behavioural change to magpilot should touch
> at minimum `.github/copilot-instructions.md` AND `docs/architecture.md`
> in the same commit. The README.md status block is touched on
> shippable milestones. plan.md is touched only on architectural
> pivots, not on incremental feature work.

The `copilot-context` repo (private, on user's local machine) holds
project files that drive ongoing work
(`copilot-context/ideas/projects/<name>.md`); keep the magpilot-side
"Ideas backlog" section below in sync with their `Status:` and
`Log:` entries when a project ships.

## Ideas backlog

<!-- Mirrored from copilot-context/ideas/inbox.md. Newest at the bottom. -->

The active backlog has been promoted out of one-liners into developed
project files in chsienki/copilot-context/ideas/projects/:

- **magpilot-ui-controls** -- expose CLI-only controls in the SPA
  (load context into a fresh session, rename, set agent mode, add dir,
  etc). /commands aren't routed through `--acp` so these need to be
  UI affordances rather than slash syntax. Distinct from preflight,
  which solves the *pre-session* "load context" case as a separate
  site -- this is the *during-session* case inside the SPA.
- **magpilot-brand-sweep** -- SHIPPED 2026-05-11. Single
  `MagpieMark` component in Magpilot.UI; brand-themed loader; bird
  on agents-list bullets + empty states.
- **magpilot-shim** -- SHIPPED 2026-05-14 across Phase 1 (agent
  endpoints), Phase 2/2.5 (magpilot launcher with PTY), Phase 3
  (SPA + WhatsApp polite-knock + take-over UX). Project file in
  copilot-context has the full design + log; the `Magpilot.Host`
  project + `HostOwnership` service + `/state`/`/release-request`/
  `/acquire-for-host`/`/release` endpoints + 409 contract on
  ACP-driving endpoints are the surfacing of it in this codebase.

(Project files are private to copilot-context. The summaries above
are the magpilot-side pointer so this codebase's agents remember
they exist and don't accidentally re-litigate them.)

Recently shipped (struck through; kept here for cross-reference so a
future "what's left?" sweep doesn't accidentally re-pick them):

- ~~2026-05-11: Magpilot UI as a Razor Class Library that magnus references directly -- no copies of code in magnus~~ -> shipped: Magpilot.UI uses Microsoft.NET.Sdk.Razor; Magnus.Web ProjectReferences it directly (magstronaut/magnus/src/Magnus.Web/Magnus.Web.csproj).
- ~~2026-05-18: visible indication when loading previous chats~~ -> partly shipped: load-more spinner in ChatView shows during demand-load; first-paint is now fast enough that the existing MudProgressLinear suffices.
- ~~2026-05-18: cache chat in local storage so only the delta of new items needs to load on subsequent opens~~ -> shipped differently: server-side tail+before paging via /history?tail=N and ?before=X&limit=N. First-paint loads tail=50; older messages demand-load on scroll-up. Same goal without an extra cache layer.
- ~~2026-05-18: online indicator via heartbeat; show 'click to reconnect' if the SSE stream drops~~ -> shipped: AppBar status pill (green/amber/red) + Offline alert with manual Reconnect button in Home.razor; 8-attempt exponential-backoff auto-reconnect on transient drops.
- ~~2026-06-08: 'yolo' toggle when creating or resuming sessions, plus per-session yolo status next to 'show thinking'~~ -> shipped: per-session YoloRegistry on the agent + POST /api/sessions/{id}/yolo + MAGPILOT_YOLO_DISABLED host kill switch + SPA toolbar switch + per-row YOLO pill. SessionInfo gained Yolo bit (decorated by SessionRegistry; in-memory only). Per-session toggle picks allow_once; env-wide MAGPILOT_AUTO_APPROVE keeps allow_always.
- ~~2026-06-08: bug -- queued messages lost on session switch / refresh; persist in local storage~~ -> shipped: queued messages AND composer draft both persisted per-session via in-memory _queueCache/_draftCache + localStorage (magpilot.queue.{agent}/{sid} + magpilot.draft.{agent}/{sid}). Session-switch snapshots OUT + loads IN; full refresh restores both. ChatView gained DraftText parameter + OnDraftChanged callback.
- ~~2026-06-09: magpilot: add an explicit 'release session' button in the SPA that calls the agent's existing /detach endpoint~~ -> shipped: Logout icon on the chat toolbar wires HubClient.DetachAsync; ergonomic placement + tooltip wrapping tracked as a v2 task under projects/magpilot-ui-controls.md.
- ~~2026-06-09: SPA gets stuck in an SSE reconnect storm when navigating to an unknown agent / offline agent / non-existent session id~~ -> shipped: OnParametersSetAsync pre-flights the route against _agents + _sessions and renders a NotFound state (with a Try again button) instead of falling through to the 8x retry cycle. LoadFailed also flips into session-not-found rather than appending a stray chat bubble.
- ~~2026-05-18: refresh button for the session list so you can see newer sessions without refreshing the whole page~~ -> shipped: MudIconButton in the sessions-pane header (Home.razor:171) wires to RefreshSessions; auto-refresh also fires on TurnComplete + tab-visibility events.
- ~~2026-06-08: fix overflow on repo box~~ -> shipped via MudChip + `min-width: 0` + ellipsis cap-at-180px (chip later removed when the session list moved to a 3-line title/folder/branch layout that doesn't need a chip at all -- see "drop 'past' sessions list" below). The MudChip-clipping recipe survives in the SPA / brand section of these instructions for any future caller.
- ~~2026-06-08: make 'talking' icon be the magpie~~ -> shipped: assistant + thinking-bubble avatars render `MagpieMark` inside a MudAvatar `Variant.Outlined`. Outlined keeps the chrome transparent so the multi-colour SVG isn't fighting a solid fill, while sharing the same 32x32 circle as Person/Lightbulb/Terminal avatars (so all assistant rows share a left column). The bird drops to `Size=22` so its visible content fits inside the circle's clip without losing its tail.
- ~~2026-06-09: repo names dominated by repeated `owner/` prefixes when every session is the signed-in user's own repo~~ -> shipped: SPA fetches `/api/me` on init and `DisplayRepo()` strips `{identity}/` from the chip text when it matches the signed-in user. Generic -- repos owned by anyone else still render with their full `owner/repo`. Tooltip always shows the full path so the owner is one hover away.

Open items:

- ~~2026-05-18: agent/hub connect handshake -- either a single condensed token bundling all connection info, or a UDP discovery mode (think WPS but for agents and the hub) so users don't have to copy a bunch of random strings~~ -> shipped V1 + V2a + V2b + V3 (2026-06-09, all in one day): V1 paste-bundle morning; V2a per-agent tokens + voucher TTL/single-use afternoon; V2b revocation UI + installer Pairing page late afternoon; V3 interactive UDP discovery + admin-Adopt-in-SPA + Pairing-page-removed evening. Full WPS-style pairing flow is live; install becomes "irm | iex -> browser opens -> click Adopt".
- ~~2026-06-08: cleanup status bar at top to be less confusing~~ -> shipped: extracted `Magpilot.UI/Components/HostName.razor` as the single source of truth for "host name + status indicator". AppBar variant is an outlined transparent pill whose border + dot/spinner/CloudOff colour tracks the live SSE stream state (Connected/Reconnecting/Offline); always visible. Drawer-header variant is plain inline. The redundant cloud icon + the host pill that hid behind a breakpoint are gone.
- ~~2026-06-08: powershell one-liner that downloads, verifies and runs the installer as an easy bootstrap~~ -> shipped: `scripts/install.ps1` -- fetches version.json from /releases/latest/download/, downloads the installer + .sha256, verifies, runs with UAC elevation. README has the canonical `irm ... | iex` one-liner; instructions have the parameter-passing scriptblock form for `-Silent` / `-Version` / `-Repo` / `-DryRun`. Documented draft + private-repo failure modes alongside the hub-autoupdate ones.
- ~~2026-06-08: drop 'past' sessions list, replace with 'resume previous' button that opens a filterable/searchable list~~ -> shipped: Past sessions moved out of the always-on session list into an inline "Resume previous" panel toggled by a History icon in the sessions-pane header. Searchable text filter + relative-time labels; renders inline like the new-chat panel (not as a floating dialog) so the UX matches the rest of the SPA.
- ~~2026-05-26: magpilot: handle disconnects better~~ -> shipped: SPA reacts to `release_requested` (stops stream, shows takeover banner, take-back button); on-open `/state` probe detects pre-existing host ownership; launcher fires release-request before acquire (both spawn paths) so the SPA reacts BEFORE the 409; agent's `CloseAsync` removes its `inuse.<acp-pid>.lock` so launcher's interactive copilot starts cleanly. See "Cooperative single-owner handoff" above.
- ~~2026-05-26: magpilot: session switching is broken~~ -> shipped: stale-response race fix at every `ListSessionsAsync` call site (capture agent before await, drop response if Agent changed mid-flight); session-switch instant-clear + indeterminate progress bar; on-failure error UI in the drawer instead of misleading "no sessions yet".
- 2026-06-08: combine heartbeat indicator with a 're-sync' option to recover when UI drifts from agent
- 2026-06-08: more obvious / interactive 'agent is thinking' indicator beyond the stop button and queue notification
- 2026-06-09: magpilot: new-session in a non-existent cwd returns 500 from POST /api/sessions -- should be a friendly "this directory doesn't exist; create it?" dialog (Yes -> mkdir + retry, No -> back to dialog with field highlighted). Likely folds into the richer pre-flight checks of the magpilot-ui-controls redesign.
- 2026-06-15: chat-pane hierarchy refactor -- thinking should not dominate, tool calls should attach to assistant turns. Three independent design critiques (Claude Opus / GPT-5.4 / Gemini 3.1 Pro) all flagged the same root issue: thinking blocks (italic walls), assistant turns, and tool-call chips use near-identical containers, so the eye can't infer "what happened" vs "what was thinking" vs "what tools ran" at a glance. Punch list with concrete fixes saved as a session-state artifact.
- 2026-06-15: AppBar duplicates the host name (pill + breadcrumb + drawer header). HENDRIK appears in three places; pick one source of truth (likely keep the pill as the live-status surface and drop the breadcrumb prefix).
- 2026-06-15: vertical alignment of the chat-toolbar toggles vs the release icon -- "Show thinking" + "YOLO" toggles on the left don't share a baseline with the Logout / release icon on the right.
