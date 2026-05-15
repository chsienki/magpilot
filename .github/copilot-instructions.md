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
                                      a magpilot-host wrapper holds. The
                                      filesystem inuse.lock files are
                                      advisory (see "lock files are NOT
                                      a mutex" gotcha below).
    Sessions/HistoryReader.cs      <- reads events.jsonl directly so the
                                      SPA can rehydrate Owned-without-cache
    Logging/HubLoggerProvider.cs   <- mirrors Warning+ to hub /api/log/batch
  Magpilot.Hub/      <- central daemon: agent registry, proxy, SPA host, auth
    Logging/LogStore.cs            <- SQLite + FTS5 central log; trim loop
    Logging/LogModels.cs           <- ingest + query DTOs
    Api/LogEndpoints.cs            <- POST /api/log[/batch], GET /api/log[/sources]
    Api/HubEndpoints.cs            <- proxies the agent endpoints, including
                                      the four shim routes (/state,
                                      /release-request, /acquire-for-host,
                                      /release).
    Auth/HubAuth.cs                <- cookie auth + GitHub OAuth (with
                                      ReturnUrl bounce for satellite SPAs).
  Magpilot.Host/     <- magpilot-host wrapper (assembly: magpilot-host).
                       Aliased as `copilot` on PATH; coordinates with
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
    Pages/Home.razor               <- main chat (per-session cache, SSE consumer,
                                      three-way Owned routing -- see SPA section).
                                      HandleSend catches HostStillOwnedException
                                      and surfaces a "Take over from terminal"
                                      MudAlert (see "Cooperative handoff").
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
| `MAGPILOT_HUB_TRUSTED_PROXIES` | hub | Comma list of IPs allowed to set `X-Forwarded-*` (NPM IP). Defaults to `127.0.0.1,::1` -- **required** when NPM and the hub run as containers on the same LXC under `network_mode: host`, since NPM's request reaches the hub from `127.0.0.1` not the LXC's external IP. Without this, `X-Forwarded-Proto: https` is dropped, OAuth `redirect_uri` becomes `http://...` and GitHub rejects. |
| `MAGPILOT_HUB_COOKIE_DOMAIN` | hub (optional) | Sets the auth cookie's `Domain` attribute. Use a leading-dot value like `.home.sienkiewi.cz` to share sign-in across satellite SPAs hosted on sibling subdomains (e.g. magnus.home.sienkiewi.cz). Leave unset for plain dev runs at localhost. |
| `OAUTH_CLIENT_ID` / `OAUTH_CLIENT_SECRET` | hub | GitHub OAuth App credentials. Without these the hub serves `/login -> 'OAUTH_CLIENT_ID not configured'`. |
| `OAUTH_ALLOWED_GITHUB_USERS` | hub | Comma list of GitHub logins the hub allows. Anyone else gets denied after OAuth. |
| `MAGPILOT_AGENT_URL` | magpilot-host wrapper | Default `http://127.0.0.1:5099`. Where the wrapper reaches its local agent. |
| `MAGPILOT_REAL_COPILOT` | magpilot-host wrapper (optional) | Explicit path to the real `copilot` binary. Useful when the wrapper is aliased to `copilot` and PATH resolution would recurse. |

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
> **Authoritative ownership lives in agent memory, NOT in the
> filesystem.** `Sessions/HostOwnership.cs` keeps the canonical map
> of `sessionId -> hostPid` for sessions a `magpilot-host` wrapper
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

### Cooperative single-owner handoff (magpilot-host)

The shim project. A `magpilot-host` wrapper (in `src/Magpilot.Host`)
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
  id, timestamp } }`. Cheap. Wrapper calls on every startup.
- **`POST /api/sessions/{id}/release-request`** -- body
  `ReleaseRequestBody { Requester, Force }`. Broadcasts a
  `ReleaseRequested` SSE event on the session's stream so any
  subscribed wrapper can begin its graceful shutdown. Idempotent.
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
  sections). Pick a host -> sessions pane with a back-arrow header
  + the agent's status bullet + the New chat button. Back arrow
  flips `_showHostsPane` without touching the URL or the open chat.
  Don't merge them back into one tall list.

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

- The `CopilotChat.Maui` Android shell -- no project yet, but the
  shared `Magpilot.UI` is designed to drop into a MAUI Blazor Hybrid
  WebView later.
- Real FCM push and Web Push (VAPID) delivery.
- TLS between hub and per-host agents (currently plaintext over LAN +
  bearer token).
- Approval-prompt modals for risky tool calls.
- Sidecar-side log forwarders (the hub sink + agent forwarder are
  in; sidecars are still TODO).
- Win32 Job Object for ACP-child cleanup on Windows agents.
- magpilot-host wrapper extras: `--magpilot-status` only prints
  reachability so far (full session listing TBD); no-args picker
  (list sessions for cwd) + `--continue` not implemented; wrapper
  doesn't post to `/api/log` audit trail yet.
- Pattern gamma "fake IDE" (agent advertises its own
  `~/.copilot/ide/<sid>.lock` so the unmodified copilot binary
  auto-attaches and the agent intercepts the 6-tool MCP-over-pipe
  callback surface). Designed in the shim doc; deferred until SPA
  diff-review becomes a recurring ask.

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
  endpoints), Phase 2/2.5 (magpilot-host wrapper with PTY), Phase 3
  (SPA + WhatsApp polite-knock + take-over UX). Project file in
  copilot-context has the full design + log; the `Magpilot.Host`
  project + `HostOwnership` service + `/state`/`/release-request`/
  `/acquire-for-host`/`/release` endpoints + 409 contract on
  ACP-driving endpoints are the surfacing of it in this codebase.

(Project files are private to copilot-context. The summaries above
are the magpilot-side pointer so this codebase's agents remember
they exist and don't accidentally re-litigate them.)
- 2026-05-11: Magpilot UI as a Razor Class Library that magnus references directly -- no copies of code in magnus
