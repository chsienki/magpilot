# Clawpilot — Copilot agent instructions

> Read this first. It captures the architecture, the moving parts, the
> non-obvious gotchas, and the build/deploy workflow you need to make
> changes confidently. The README is for humans; this file is for you.

## What this repo is

Clawpilot puts the GitHub Copilot CLI on the user's phone (and any
browser) by:

1. Running `copilot --acp` (Agent Client Protocol over JSON-RPC on
   stdio) on each real machine, wrapped by a small **per-host agent**
   daemon that exposes an HTTP+SSE API to the LAN.
2. A central **hub** daemon on the docker LXC (102, `192.168.1.239`)
   that auto-discovers agents over UDP broadcast, aggregates their
   sessions, proxies streams, and serves the web SPA + handles auth.
3. Clients (a Blazor WebAssembly SPA today; a MAUI Blazor Hybrid
   Android shell later) consume the hub's API.

The user-facing URL is `https://clawpilot.home.sienkiewi.cz`
(LAN/WireGuard only, fronted by NPM on the same LXC).

## Project layout

```
src/
  Clawpilot.Shared/   <- DTOs, SSE event types (StreamEvent discriminator)
  Clawpilot.Agent/    <- per-host daemon: ACP client + minimal HTTP/SSE API
    Acp/AcpSessionManager.cs       <- the heart; ACP <-> SSE translation
    Api/AgentEndpoints.cs          <- HTTP endpoints (sessions, /messages, SSE)
    Sessions/SessionScanner.cs     <- discovers Past/LiveOwned/LiveOrphan sessions
  Clawpilot.Hub/      <- central daemon: agent registry, proxy, SPA host, auth
  Clawpilot.UI/       <- shared Blazor components
    Pages/Home.razor               <- main chat page (per-session message cache, SSE consumer)
    Components/ChatView.razor      <- renders messages (assistant=markdown, thought=details)
    Components/MarkdownView.razor  <- Markdig wrapper
  Clawpilot.Web/      <- Blazor WASM shell (built into Clawpilot.Hub/wwwroot/)
deploy/               <- docker-compose + deploy notes for LXC 102
docs/plan.md          <- full design doc (v5: Blazor Hybrid + Web). Read this for context.
spikes/acp-smoke/     <- standalone ACP smoke test (Node.js)
scripts/build-hub.ps1 <- publishes Web SPA -> copies into Hub/wwwroot
```

`Clawpilot.slnx` (XML solution format) is the solution. There is no
`.sln`. `dotnet build` / `dotnet test` understand `.slnx`.

## Build, run, deploy

```pwsh
# Build everything
dotnet build

# Run the agent (terminal 1; on the host whose Copilot CLI you want to drive)
$env:CLAWPILOT_AGENT_TOKEN      = "dev-token"
$env:CLAWPILOT_AGENT_PUBLIC_URL = "http://localhost:5099"   # what the hub uses to reach back
$env:ASPNETCORE_URLS            = "http://localhost:5099"
dotnet run --project src/Clawpilot.Agent

# Build SPA + copy into hub wwwroot, then run hub (terminal 2)
./scripts/build-hub.ps1
$env:CLAWPILOT_HUB_BEARER       = "dev-bearer"
$env:CLAWPILOT_AGENT_TOKEN      = "dev-token"
$env:CLAWPILOT_DEV_BYPASS_AUTH  = "true"
$env:ASPNETCORE_URLS            = "http://localhost:7088"
dotnet run --project src/Clawpilot.Hub
# Open http://localhost:7088
```

Hot-reload UI work: `dotnet watch --project src/Clawpilot.Web` does NOT
go through the hub's API. For end-to-end UI dev use the inspect skill
(`run-and-inspect`) against `Clawpilot.Web` proxied to the hub, or
just rebuild SPA + restart hub.

### Deploying the hub to the LXC

`deploy/README.md` is the canonical recipe. Short version:

1. `docker buildx build --platform linux/amd64 -f src/Clawpilot.Hub/Dockerfile -t clawpilot-hub:latest --load .`
2. `docker save` -> `scp` to proxmox -> `pct push 102` -> `docker load` inside the container.
3. `pct exec 102 -- bash -c 'cd /srv/clawpilot && docker compose up -d'`

**Critical `tar`/exclude gotcha when shipping source**: do NOT exclude
`**/wwwroot` blanket-style — that kills `Clawpilot.Web/wwwroot/index.html`
which is real source. Only exclude `src/Clawpilot.Hub/wwwroot/` (the
build output). The `.gitignore` reflects this.

The agent runs on each host directly (no docker). On HENDRIK it is a
plain `dotnet run` in an `async` PowerShell session named `agent`.

## Environment variables

| Var | Where | Purpose |
|---|---|---|
| `CLAWPILOT_AGENT_TOKEN` | agent + hub | Shared bearer secret hub uses to call agents. **Must match.** |
| `CLAWPILOT_AGENT_PUBLIC_URL` | agent | URL the agent broadcasts in UDP discovery so the hub can call back. |
| `CLAWPILOT_HUB_BEARER` | hub | Bearer for non-OAuth API calls (curl, MAUI app). |
| `CLAWPILOT_DEV_BYPASS_AUTH` | hub | When `"true"`, skips OAuth (dev only). |
| `CLAWPILOT_HUB_DATA` | hub | Directory for `hub.db`. Defaults to `./data`. |
| `CLAWPILOT_HUB_TRUSTED_PROXIES` | hub | Comma list of IPs allowed to set `X-Forwarded-*` (NPM IP). |

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

### Session classification (`SessionScanner`)

A copilot session on disk has an `inuse.<PID>.lock` file when held by
a live process. We classify into:

- **LiveOwned** — lock PID is one we (this agent) spawned. We own it.
- **LiveOrphan** — lock PID is alive but is some other copilot
  process (e.g. a terminal session the user opened). To adopt, we
  must `Stop-Process -Id <PID>` it then `session/load` ourselves.
- **Past** — no lock, or the lock PID is dead. Free to `session/load`.

`UpdatedAt` is derived from the latest mtime between `events.jsonl`
and `workspace.yaml`. **Do NOT** trust the `updated_at` field inside
`workspace.yaml` — it isn't rewritten on every message. The
`/api/sessions` endpoint sorts DESC by `UpdatedAt`.

### SPA session-state contract (`Home.razor`)

The SPA keeps a per-session message cache (`_msgCache:
Dictionary<sessionKey, List<ChatMessage>>`). On switch:

- **LiveOwned + cache present**: restore from cache, call
  `AdoptAsync(load: false)`. **Never** call `session/load` here.
- **Past or LiveOrphan**: call `AdoptAsync(load: true)` to replay
  history; that history is then authoritative.

The SPA is the only producer of new visible messages while the user
is connected, so the cache is safe as last-word for `LiveOwned`.
When the agent restarts, *all* sessions become Past/LiveOrphan and
get a fresh `session/load` on next visit.

### The "input never re-enables" pitfall

Anywhere you wait for a turn to end, you MUST observe the SSE
`TurnComplete` event. Don't tie input enablement to the HTTP
response of `/messages` — that returns 202 immediately.

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

## Style and conventions

- Target framework: `net9.0` for everything. C# language version: latest.
- File-scoped namespaces, primary constructors, pattern matching, `var`
  when type is obvious, collection expressions, raw string literals.
- xUnit for tests (none yet — when you add them, follow AAA and use
  `Fact`/`Theory`).
- ASCII only in source comments and string literals (PowerShell-side
  encoding can mangle non-ASCII characters in commit pipelines).
- Keep DTOs in `Clawpilot.Shared`. The SSE wire format
  (`StreamEvent` discriminated union) is the contract between agent
  and SPA — bumping it requires both sides to ship together.

## Useful operational commands

```pwsh
# Tail the hub logs on the LXC
ssh proxmox "pct exec 102 -- docker logs --tail 200 -f clawpilot-hub"

# Restart the agent on HENDRIK (in the existing 'agent' async session)
#  (in the agent shell: Ctrl+C, then `dotnet run --project src/Clawpilot.Agent`)

# Check NPM proxy host config for clawpilot
$tok = (Invoke-RestMethod -Uri http://192.168.1.239:81/api/tokens -Method Post `
        -ContentType application/json `
        -Body (@{identity="<email>"; secret="<pwd>"}|ConvertTo-Json)).token
Invoke-RestMethod -Uri http://192.168.1.239:81/api/nginx/proxy-hosts/7 `
                  -Headers @{Authorization="Bearer $tok"}
```

## What is NOT yet built (don't assume it exists)

- The `CopilotChat.Maui` Android shell — no project yet, but the
  shared `Clawpilot.UI` is designed to drop into a MAUI Blazor Hybrid
  WebView later.
- Real FCM push and Web Push (VAPID) delivery.
- TLS between hub and per-host agents (currently plaintext over LAN +
  bearer token).
- Approval-prompt modals for risky tool calls.

If you implement any of these, update `docs/plan.md` and this file.

## When in doubt

1. Read `docs/plan.md`. It is the authoritative design doc.
2. The home-network and openclaw operational context lives in a
   separate repo at `chsienki/copilot-context` (not on the cloud
   agent — only the user's local machine).
3. Don't commit without explicit user permission. The user prefers
   to review changes before they hit `main`.
