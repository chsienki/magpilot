# Phone-to-Copilot-CLI Relay — Design Plan (v4: ACP-based)

> A purpose-built .NET MAUI Android app + central hub on the docker LXC +
> per-host agent daemons that gives Chris a chat client on his Pixel,
> talking to real `copilot` CLI processes on **any of his computers** over
> WireGuard or LAN.
>
> **v4 (2026-04-25):** Replaced PTY wrapping with the official
> **Agent Client Protocol (ACP)** transport. Copilot CLI exposes `--acp`
> in stdio or TCP mode and speaks JSON-RPC 2.0 with first-class session
> lifecycle (`session/new`, `session/load`, `session/resume`,
> `session/close`), structured streaming updates, and a built-in
> permission-request mechanism. Per-host agent becomes a thin
> ACP-to-HTTP/SSE adapter; one `copilot --acp` process per host hosts
> all sessions. No ANSI parsing, no per-session subprocesses.

---

## Confirmed requirements

1. **Form factor:** Native phone app, .NET MAUI (Android target for v1).
2. **Capability:** Full Copilot CLI agent — tool calls, file edits, shell,
   git, MCP servers.
3. **Sessions:** Persistent, multi-day, resumable. Multiple concurrent
   chats. Survives phone restarts and host reboots.
4. **Notifications:** Push when (a) long task finishes, (b) agent asks a
   question while app is backgrounded, (c) any approval prompt fires.
5. **Network:** Phone always connects to **one address** — the hub on
   the docker LXC (`192.168.1.239`). Hub aggregates all hosts.
6. **Multi-host:** Hub auto-discovers per-host agents on the LAN via
   UDP broadcast. Manual add as fallback.
7. **Session takeover:** When a Copilot session is already running on
   a host (detected by `inuse.<PID>.lock`), the phone can adopt it —
   the per-host agent kills the foreground process and `session/load`s
   the same id under its own ACP-managed copilot process.
8. **Past sessions:** Phone lists historical sessions on each host
   (those without an `inuse.*.lock`) so the user can resume any of them
   or start fresh.

---

## What ACP gives us (and why this is now the design)

### Lifecycle methods (agent-side, called by us)

| Method | When we call it | Meaning |
|---|---|---|
| `initialize` | Once on agent boot per copilot child | Capability negotiation |
| `session/new` | "+ New chat" on the phone | Fresh session, returns `sessionId` |
| `session/load` | Phone opens a Past session | Replays full history via `session/update` notifications, then resolves |
| `session/resume` | Phone re-attaches to a session we own that hasn't been chatted with this hour | Reconnect without replay |
| `session/prompt` | Phone sends a user message | Standard request |
| `session/cancel` | Phone hits "stop" | In-flight cancellation |
| `session/close` | Phone discards a session | Frees agent resources |

### Notifications (agent-side, sent to us)

| Notification | What we do |
|---|---|
| `session/update` (`agent_message_chunk`) | Forward to phone over SSE as `assistant_delta` |
| `session/update` (`tool_call_start`/`tool_call_progress`/`tool_call_end`) | Forward as structured `tool_call_*` events |
| `session/update` (`user_message_chunk`, on `session/load`) | Build past history server-side, send full pages to phone |

### Client methods we MUST implement (called by the agent)

| Method | v1 strategy |
|---|---|
| `session/request_permission` | Forward as SSE `approval_required`; phone shows modal; reply with user's choice |
| (optional) `fs/read_text_file`, `fs/write_text_file`, `terminal/*` | **Skip in v1** — let copilot use its built-in filesystem and shell tools directly on the host. The agent process runs as Chris on the host, so this Just Works. v2 may proxy these for sandboxing. |

### Why one ACP process per host (not per session)

- ACP multiplexes sessions natively (every `session/*` method takes a
  `sessionId`).
- Spawn cost is non-trivial; one long-lived `copilot --acp --port N` is
  cheaper.
- Failure isolation is fine: if a single session goes bad we
  `session/close` it; the others stay alive.
- Restart story: if the copilot process crashes, agent re-spawns it
  and `session/load`s every session that was active. We never lose
  state because session-state lives on disk in `~/.copilot/session-state/`.

---

## High-level architecture

```
   +--- Pixel 10 Pro ---------+      WG / LAN      +--- docker LXC (102) ----+
   |                          |   (HTTPS + SSE)    |  copilot-hub (.NET 9)   |
   |  CopilotChat (.NET MAUI) | <----------------> |   - aggregates agents   |
   |   - host+session tree    |                    |   - LAN UDP discovery   |
   |   - chat / tool cards    |                    |   - SSE proxy           |
   |   - approval modals      |                    |   - FCM sender          |
   |   - FCM receiver         |                    +-----+-------------------+
   +-----+--------------------+                          |
         ^                                               | HTTPS + UDP discovery
         |                                               v
         |               +----+ +----+ +-------------+
         |               | HE | | Mac| | future host |
         |               | ND | |    | |             |
         |               | RIK| |    | |             |
         |               +----+ +----+ +-------------+
         |               copilot-agent (.NET 9)
         |                  |
         |                  | speaks ACP (JSON-RPC) over TCP loopback
         |                  v
         |               +-----------------------+
         |               | copilot --acp         |
         |               |   --port <ephemeral>  |
         |               | (one per host, multi- |
         |               |  session)             |
         |               +-----------------------+
         |                          FCM v1
         +------------------------------+
```

---

## Component 1 — `copilot-agent` (per-host daemon)

**Language:** .NET 9 single-file publish.

**Lives on:** every computer that runs Copilot CLI.

### 1.1 Bootstrap

1. Spawn `copilot --acp --port <ephemeral-loopback>` as a child process.
2. Open TCP connection to that port.
3. Send `initialize` and capture `agentCapabilities` (we expect
   `loadSession: true`, `sessionCapabilities.resume`,
   `sessionCapabilities.close`).
4. Stay connected for the lifetime of the daemon.

### 1.2 Session enumeration (no PTY, no process scanning needed)

```pseudocode
foreach dir in ~/.copilot/session-state/*/:
    workspace = parse workspace.yaml
    lock = glob(dir, "inuse.*.lock").FirstOrDefault()
    category = lock != null ? "live-orphan" : "past"
    pid      = lock != null ? int.Parse(lock.Name.Split('.')[1]) : null
    yield {
        id: workspace.id,
        title: workspace.summary,
        repo: workspace.repository,
        branch: workspace.branch,
        cwd: workspace.cwd,
        created_at, updated_at,
        category,                // live-orphan | live-owned | past
        owning_pid: pid,
        events_size: filesize(dir/events.jsonl),
    }
```

(The agent also tracks `live-owned` for sessions it's already
`load`ed/`resume`d into its own ACP child.)

### 1.3 Session takeover (orphan adoption)

When the hub asks the agent to attach to a `live-orphan` session:

1. Verify lock file exists and `owning_pid` is alive.
2. Surface PID/cwd/summary back to the phone for "kill and adopt?"
   confirmation.
3. On confirm: SIGTERM by PID → wait 5s → SIGKILL → wait for lock
   file to disappear.
4. Call ACP `session/load` with the same id and original `cwd`.
5. Stream the replayed history (`session/update` notifications with
   `user_message_chunk`/`agent_message_chunk`) to the phone as a
   "history" SSE block, followed by `history_done`.
6. Session is now `live-owned`; further prompts/streams are normal.

### 1.4 Past-session resume

Same as takeover but skip steps 1–3 (no orphan to kill).

### 1.5 New session

1. Phone POSTs new chat with optional `cwd` (defaults to user's home).
2. Agent calls ACP `session/new`, captures returned `sessionId`.
3. Returns id to hub → phone.

### 1.6 Live history rendering for past sessions WITHOUT resuming

Phone may want to scroll back through a past session without spawning
anything (e.g., "remind me what I did last Tuesday"). Two options:

- **A:** Read `events.jsonl` directly on the agent and stream it to
  the phone as structured history. No ACP call needed. Works on any
  past session.
- **B:** Call `session/load` (which does this for us) but immediately
  `session/close` once history finishes. Wastes a little CPU but uses
  the official mechanism.

**v1 picks A** because it lets the phone browse old chats without
incurring agent process load. Falls back to B if `events.jsonl` schema
changes.

### 1.7 Per-agent HTTP API (consumed only by the hub)

```
GET    /info                              { hostname, os, copilot_version, agent_version, uptime, acp_capabilities }
GET    /sessions                          enumerated sessions w/ category
POST   /sessions                          new (ACP session/new)
GET    /sessions/:id                      single, with metadata
GET    /sessions/:id/history              raw events.jsonl decoded (no ACP)
POST   /sessions/:id/attach               kill+load if orphan, load if past, no-op if owned. Returns SSE-stream URL.
POST   /sessions/:id/detach               session/close + delete agent's tracking (file remains on disk; becomes "past")
DELETE /sessions/:id                      attach if needed → session/close → rm -rf session-state dir
POST   /sessions/:id/messages             ACP session/prompt
GET    /sessions/:id/stream               SSE: assistant_delta, tool_call_*, approval_required, assistant_done, idle
POST   /sessions/:id/approvals/:aid       resolve a pending session/request_permission
POST   /sessions/:id/interrupt            ACP session/cancel
```

### 1.8 Discovery beacon

Listens on UDP 52789 on all non-loopback, non-WG interfaces.
Responds to discovery query with `/info` payload + TLS fingerprint
+ HTTPS port.

### 1.9 Auth

Per-install bearer token; rotatable via `copilot-agent print-token`.
Hub stores per-agent tokens; phone never sees them.

### 1.10 Crash resilience

If the `copilot --acp` child dies, agent:

1. Marks all `live-owned` sessions as transiently broken.
2. Re-spawns child + `initialize`.
3. For each previously-owned session: `session/load` to restore.
4. Phones reconnecting to the SSE stream see no permanent break;
   buffered output (lost during the crash window) is replayed via
   `events.jsonl` since the last known event id.

### 1.11 What about copilot's own approval flow?

Copilot CLI normally pops approval TUI prompts. In ACP mode it
issues `session/request_permission` to the client (us) instead. We
relay that to the phone, the user picks Allow/Deny/Always-allow,
and we reply. **No `--allow-all-tools` blanket — we get real,
per-tool approvals on the phone.** This is materially better than
the PTY approach.

---

## Component 2 — `copilot-hub` (central daemon, on docker LXC)

Unchanged from v3 except:

- Hub no longer needs to know anything about PTYs or ANSI. It's a
  pure proxy + aggregator.
- Hub's SSE schema mirrors the per-agent SSE schema 1:1.
- Hub still owns: address book, LAN discovery, FCM, phone-facing
  bearer auth, sqlite cache.
- `network_mode: host` in compose for UDP broadcast.

(See v3 for full Hub responsibilities and API — they don't change.)

---

## Component 3 — `CopilotChat` (.NET MAUI app)

Unchanged from v3 in shape, but the structured nature of ACP makes
the chat-screen UI substantially nicer:

- Tool-call cards have **real** start/progress/end events (with
  args + result content blocks), not heuristic ANSI parsing.
- Approval modals are **deterministic** — every `session/request_permission`
  has a unique id we can correlate.
- Past-session view shows a clean, faithfully-replayed transcript
  (since `session/load` semantics are well-defined).

---

## Component 4 — Push notifications

Unchanged from v3. Hub receives `approval_required` and late
`assistant_done` from agents and fans out FCM data-only payloads.

---

## Component 5 — Networking & security

Unchanged from v3. Phone↔hub TLS+bearer. Hub↔agents TLS+bearer.
ACP child speaks loopback only.

---

## Component 6 — Operations & install

Unchanged from v3 in deployment topology:
- Hub: docker LXC 102, in compose.
- Agents: Windows service / launchd / systemd via
  `copilot-agent install --service`.
- App: APK via Obtainium.

---

## Open questions / decisions to revisit

1. **Does `session/load` work for any past Copilot CLI session id?**
   The on-disk session id (`workspace.yaml:id`) and ACP `sessionId`
   appear to be the same UUID, but spike to confirm. If they differ,
   we'd need to translate — possibly via an extra Copilot CLI
   subcommand or by reading `events.jsonl` ourselves.
2. **MCP servers in `session/new` / `session/load` calls.** ACP wants
   us to provide an MCP server list. Default to the user's existing
   `~/.copilot/mcp-config.json` (read by agent, passed to ACP).
3. **`session/request_permission` UX.** Each request comes with
   options (allow once / always / deny / etc.). Mirror those on the
   phone exactly. Don't invent our own.
4. **`fs/*` vs built-in tools.** v1 = skip; v2 may want to proxy so
   the relay can log/sandbox file ops. Keep API extensible.
5. **`session/cancel` granularity.** ACP cancel is per-session, not
   per-tool-call. Confirm that's what users expect when they hit "stop".
6. **Concurrent phones.** Two phones attaching to the same session
   both want the SSE stream. Hub fan-out is straightforward; ACP
   itself is single-client per session, so the agent multiplexes
   notifications to N phone connections.
7. **Comparison to GitHub's own `--remote` story.** `--remote` ties
   sessions to GitHub web/mobile via `mc_session_id`. We deliberately
   ignore it (privacy, control), but we should test that having a
   session marked `remote_steerable: true` doesn't conflict with
   ACP `session/load` from a separate process.

---

## What we're NOT doing (vs. openclaw)

- ❌ Plugin system
- ❌ Cron / scheduled agent jobs
- ❌ WhatsApp / multi-channel delivery
- ❌ MCP server registry / web UI for config
- ❌ Smart home control
- ❌ Multi-tenant or multi-user

---

## Suggested build order

1. **Spike A — ACP smoke test.** Use the official TypeScript
   client (or roll a tiny .NET ACP client) to:
   - Spawn `copilot --acp --port N`
   - `initialize` + `session/new` + `session/prompt "hello"` and
     stream the response.
   - Then start a session in a regular terminal, kill it, and try
     `session/load` against its id from the ACP server. Confirm
     bidirectional id compatibility. **Risk-retiring the entire design.**
2. **`copilot-agent` v0.** Single host, one ACP child, the eight
   API endpoints from §1.7, no auth, no TLS. Test from `curl`.
3. **`copilot-hub` v0.** Pure proxy; hardcoded single agent.
4. **MAUI app shell.** Onboarding + sessions tree + chat + approvals.
   End-to-end via hub against one host.
5. **Multi-agent + LAN UDP discovery.**
6. **Orphan adoption + past-session resume + history rendering.**
7. **TLS + TOFU pinning everywhere.**
8. **FCM push from hub.**
9. **Service install across Win/macOS, hub container in compose.**
10. **iOS target** (free with MAUI). TestFlight.

Each step shippable independently.
