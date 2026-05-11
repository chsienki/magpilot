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
  Magpilot.Shared/   <- DTOs, SSE event types (StreamEvent discriminator)
  Magpilot.Agent/    <- per-host daemon: ACP client + minimal HTTP/SSE API
    Acp/AcpSessionManager.cs       <- the heart; ACP <-> SSE translation
    Acp/AcpClient.cs               <- one process. Resolves exe full path
                                      (Process.Start launcher-shim fix),
                                      reads settings.json and forwards
                                      --plugin-dir per enabled plugin.
    Acp/AcpFlavorPool.cs           <- one client per AcpFlavor key
    Api/AgentEndpoints.cs          <- HTTP endpoints (sessions, /messages,
                                      /history, /quick-prompt, /stream SSE)
    Sessions/SessionScanner.cs     <- discovers Owned/Locked/Dormant sessions
    Sessions/HistoryReader.cs      <- reads events.jsonl directly so the
                                      SPA can rehydrate Owned-without-cache
    Logging/HubLoggerProvider.cs   <- mirrors Warning+ to hub /api/log/batch
  Magpilot.Hub/      <- central daemon: agent registry, proxy, SPA host, auth
    Logging/LogStore.cs            <- SQLite + FTS5 central log; trim loop
    Logging/LogModels.cs           <- ingest + query DTOs
    Api/LogEndpoints.cs            <- POST /api/log[/batch], GET /api/log[/sources]
  Magpilot.UI/       <- shared Blazor components (RCL, MudBlazor 9.4)
    MagpilotTheme.cs               <- light + dark MudThemes (deep midnight blue)
    ThemeState.cs                  <- cascading dark-mode toggle
    Pages/Home.razor               <- main chat (per-session cache, SSE consumer,
                                      three-way Owned routing -- see SPA section)
    Pages/Logs.razor               <- /admin/logs viewer
    Components/ChatView.razor      <- renders messages (assistant=markdown, thought=details)
    Components/MarkdownView.razor  <- Markdig wrapper
    Services/HubClient.cs          <- per-session/agent API calls
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
$env:MAGPILOT_AGENT_TOKEN      = "dev-token"
$env:MAGPILOT_DEV_BYPASS_AUTH  = "true"
$env:ASPNETCORE_URLS            = "http://localhost:7088"
dotnet run --project src/Magpilot.Hub
# Open http://localhost:7088
```

Hot-reload UI work: `dotnet watch --project src/Magpilot.Web` does NOT
go through the hub's API. For end-to-end UI dev use the inspect skill
(`run-and-inspect`) against `Magpilot.Web` proxied to the hub, or
just rebuild SPA + restart hub.

### Deploying the hub to the LXC

`deploy/README.md` is the canonical recipe. Short version:

1. `docker buildx build --platform linux/amd64 -f src/Magpilot.Hub/Dockerfile -t magpilot-hub:latest --load .`
2. `docker save` -> `scp` to proxmox -> `pct push 102` -> `docker load` inside the container.
3. `pct exec 102 -- bash -c 'cd /srv/magpilot && docker compose up -d'`

**Critical `tar`/exclude gotcha when shipping source**: do NOT exclude
`**/wwwroot` blanket-style — that kills `Magpilot.Web/wwwroot/index.html`
which is real source. Only exclude `src/Magpilot.Hub/wwwroot/` (the
build output). The `.gitignore` reflects this.

The agent runs on each host directly (no docker). On HENDRIK it is a
plain `dotnet run` in an `async` PowerShell session named `agent`.

## Environment variables

| Var | Where | Purpose |
|---|---|---|
| `MAGPILOT_AGENT_TOKEN` | agent + hub | Shared bearer secret hub uses to call agents. **Must match.** |
| `MAGPILOT_AGENT_PUBLIC_URL` | agent | URL the agent broadcasts in UDP discovery so the hub can call back. |
| `MAGPILOT_AGENT_NAME` | agent (optional) | Source label used for central log forwarding. Defaults to hostname. |
| `MAGPILOT_HUB_URL` | agent (optional) | Hub base URL the agent posts forwarded logs to (e.g. `http://192.168.1.239:7088`). Forwarder is a no-op if unset. |
| `MAGPILOT_HUB_BEARER` | hub + non-cookie clients | Bearer secret the hub validates for API calls without a session cookie (agents, sidecars, curl, MAUI app). Required for `/api/log` ingest from non-SPA sources. **In active use** -- set it. |
| `MAGPILOT_DEV_BYPASS_AUTH` | hub | When `"true"`, skips OAuth (dev only -- redirects `/login` to `/dev-login`). |
| `MAGPILOT_HUB_DATA` | hub | Directory for `hub.db` and `logs.db`. Defaults to `./data`. |
| `MAGPILOT_HUB_TRUSTED_PROXIES` | hub | Comma list of IPs allowed to set `X-Forwarded-*` (NPM IP). |
| `MAGPILOT_HUB_COOKIE_DOMAIN` | hub (optional) | Sets the auth cookie's `Domain` attribute. Use a leading-dot value like `.home.sienkiewi.cz` to share sign-in across satellite SPAs hosted on sibling subdomains (e.g. magnus.home.sienkiewi.cz). Leave unset for plain dev runs at localhost. |

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

### Session classification (`SessionScanner`)

A copilot session on disk has an `inuse.<PID>.lock` file when held by
a live process. We classify into:

- **Owned** — lock PID is one we (this agent) spawned. We own it.
- **Locked** — lock PID is alive but is some other copilot
  process (e.g. a terminal session the user opened). To adopt, we
  must `Stop-Process -Id <PID>` it then `session/load` ourselves.
- **Dormant** — no lock, or the lock PID is dead. Free to `session/load`.

(Wire format is the integer ordinal of `SessionState`. Order matters:
Owned=0, Locked=1, Dormant=2.)

> **⚠️ Known leak: copilot's lock file is advisory.** A live
> `copilot.exe` (especially via `--resume`) can keep appending to a
> session's `events.jsonl` *without* refreshing or owning an
> `inuse.<PID>.lock`. The result: the scanner sees no live lock,
> classifies the session as `Dormant`, and the agent will happily
> `session/load` it into our ACP child — silently sharing the session
> with a still-running CLI. Both processes then write the same
> `events.jsonl` with no mutual exclusion. Observed on 2026-04-28
> with this very session: original CLI (pid 165200) died, a `--resume`
> CLI took over without re-locking, agent loaded the session into ACP
> (pid 100916), and the web UI offered to drive a session that was
> already live in a terminal. **If this becomes a recurring problem,
> fix by treating a fresh `events.jsonl` mtime (e.g. modified within
> the last 30s) as a liveness signal in addition to the lock file —
> classify as `Locked` and refuse silent adoption.** For now, just
> be aware.

`UpdatedAt` is derived from the latest mtime between `events.jsonl`
and `workspace.yaml`. **Do NOT** trust the `updated_at` field inside
`workspace.yaml` — it isn't rewritten on every message. The
`/api/sessions` endpoint sorts DESC by `UpdatedAt`.

`SessionScanner` has a tiny line-based YAML parser that **unfolds
`|-` block scalars** when reading `name`/`summary`. Without that, a
pinned session whose name starts with a quote or contains a newline
renders in the SPA list with the literal `|-` as its title.

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
- **Brand mark**: `Magpilot.Web/wwwroot/favicon-mark.svg` is a
  bg-stripped magpie. The favicon and AppBar chip both use it.

## Operational gotchas

- **Windows agents leak the ACP child** if the parent dies
  abnormally (taskmgr kill, Stop-Process on the dotnet PID without
  first killing the child). The orphan keeps its sessions hot and
  the next agent run sees them as Dormant-but-actually-live. Manual
  fix: `Stop-Process -Id <copilot.exe-PID>` for the orphaned child
  before restarting. Proper fix: assign each spawned ACP child to a
  Win32 Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` --
  tracked in the `agent-win-job-object` backlog item. Linux is fine
  because children inherit the parent's process group.
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

# Ship a freshly-built image to the LXC (Docker Desktop is local on Windows;
# the LXC has its own daemon, so it's a save -> scp -> pct push -> docker load).
docker save magpilot-hub:latest -o magpilot-hub.tar
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

- The `CopilotChat.Maui` Android shell — no project yet, but the
  shared `Magpilot.UI` is designed to drop into a MAUI Blazor Hybrid
  WebView later.
- Real FCM push and Web Push (VAPID) delivery.
- TLS between hub and per-host agents (currently plaintext over LAN +
  bearer token).
- Approval-prompt modals for risky tool calls.
- Sidecar-side log forwarders (the hub sink + agent forwarder are
  in; sidecars are still TODO).
- Win32 Job Object for ACP-child cleanup on Windows agents.

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
- **magpilot-brand-sweep** -- one-pass visual consolidation across
  both magpilot SPAs (magpilot.home + magnus.home): the bird becomes
  THE agents logo (drop from headers, use on agents-list bullets +
  empty states), and the default Blazor loading screen gets replaced
  with a MudBlazor-themed loader.

(Both project files are private to copilot-context. The summaries
above are the magpilot-side pointer so this codebase's agents
remember they exist and don't accidentally re-litigate them.)
- 2026-05-11: Magpilot UI as a Razor Class Library that magnus references directly -- no copies of code in magnus
