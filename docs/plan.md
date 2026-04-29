# magpilot — Design Plan (v5: Blazor Hybrid + Web)

> A purpose-built **shared Blazor UI** that runs both inside a .NET MAUI
> Android shell on the Pixel and as a WebAssembly SPA at
> `https://magpilot.home.sienkiewi.cz`, plus a central hub on the docker
> LXC and per-host agent daemons. Drives real `copilot` CLI processes on
> any of Chris's computers via the Agent Client Protocol (ACP).
>
> **v5 (2026-04-25):** Added a first-class web client. Switched UI
> strategy to **MAUI Blazor Hybrid** so the phone and web share a single
> Blazor UI codebase. Added GitHub OAuth as the web auth model and
> Web Push (VAPID) alongside FCM for notifications.
>
> **v4 (2026-04-25):** Replaced PTY wrapping with the official
> Agent Client Protocol (ACP) transport. Per-host agent is a thin
> ACP-to-HTTP/SSE adapter; one `copilot --acp` process per host.

---

## Confirmed requirements

1. **Form factor (phone):** .NET MAUI Blazor Hybrid app, Android target
   for v1.
2. **Form factor (web):** Blazor WebAssembly SPA, served by the hub at
   `https://magpilot.home.sienkiewi.cz`. **Full feature parity** with
   the phone app — anything you can do on the phone, you can do in the
   browser, and vice versa.
3. **Capability:** Full Copilot CLI agent — tool calls, file edits,
   shell, git, MCP servers.
4. **Sessions:** Persistent, multi-day, resumable. Multiple concurrent
   chats. Survives client and host restarts.
5. **Notifications:** Push when (a) long task finishes, (b) agent asks
   a question while client is backgrounded, (c) any approval prompt
   fires. FCM for Android; **Web Push (VAPID)** for browsers.
6. **Network:** All clients (phone or browser) talk to **one address** —
   the hub on the docker LXC at `https://magpilot.home.sienkiewi.cz`
   (NPM-fronted) or directly at `192.168.1.239:<port>` on LAN.
7. **Auth:**
   - **Phone:** bearer token in Android Keystore (paste once on
     install).
   - **Web:** **GitHub OAuth** ("Sign in with GitHub"), restricted to
     a single allowlisted GitHub user (`chsienki`). Cookie session.
8. **Multi-host:** Hub auto-discovers per-host agents on the LAN via
   UDP broadcast. Manual add as fallback.
9. **Session takeover:** When a Copilot session is already running on
   a host (detected by `inuse.<PID>.lock`), the client can adopt it —
   the per-host agent kills the foreground process and `session/load`s
   the same id under its own ACP-managed copilot process.
10. **Past sessions:** Clients list historical sessions per host
    (those without an `inuse.*.lock`) so the user can resume any of
    them or start fresh.

---

## What ACP gives us (and why this is the design)

### Lifecycle methods (agent-side, called by us)

| Method | When we call it | Meaning |
|---|---|---|
| `initialize` | Once on agent boot per copilot child | Capability negotiation |
| `session/new` | "+ New chat" in client | Fresh session, returns `sessionId` |
| `session/load` | Client opens a Past session | Replays full history via `session/update` notifications, then resolves |
| `session/resume` | Client re-attaches to an owned session | Reconnect without replay |
| `session/prompt` | User sends a message | Standard request |
| `session/cancel` | User hits "stop" | In-flight cancellation |
| `session/close` | User discards a session | Frees agent resources |

### Notifications (agent-side, sent to us)

| Notification | What we do |
|---|---|
| `session/update` (`agent_message_chunk`) | Forward as SSE `assistant_delta` |
| `session/update` (`tool_call_*`) | Forward as structured `tool_call_*` events |
| `session/update` (`user_message_chunk`, on `session/load`) | Build past history, send as `history_*` events |

### Client methods we MUST implement (called by the agent)

| Method | v1 strategy |
|---|---|
| `session/request_permission` | Forward as SSE `approval_required`; client shows modal; reply with user's choice |
| (optional) `fs/read_text_file`, `fs/write_text_file`, `terminal/*` | **Skip in v1** — let copilot use built-in tools directly on the host. v2 may proxy. |

### Why one ACP process per host (not per session)

ACP multiplexes sessions natively. One long-lived `copilot --acp --port N`
hosts every chat on the host. If it crashes, agent re-spawns +
`session/load`s every owned session — no state lost (lives on disk in
`~/.copilot/session-state/`).

---

## High-level architecture

```
   +----- Pixel 10 Pro -----+        +-------- Browser ---------+
   |  CopilotChat (MAUI)    |        |  https://magpilot.home  |
   |   - WebView host       |        |  Blazor WASM SPA         |
   |   - FCM receiver       |        |  Web Push receiver       |
   +-----+------------------+        +--------+-----------------+
         |                                    |
         |         shared Blazor UI lib       |
         |   (chat screen, sessions tree,     |
         |    approval modals, settings, ...) |
         |                                    |
         +-------------+----------------------+
                       |
                       | HTTPS + SSE (same API for both)
                       v
            +----- copilot-hub -------+   (docker LXC 102)
            |  - aggregates agents    |
            |  - LAN UDP discovery    |
            |  - SSE proxy            |
            |  - GitHub OAuth + cookie|
            |  - FCM + Web Push       |
            |  - serves Blazor WASM   |
            +-----+-------------------+
                  |
                  | HTTPS + UDP discovery
                  v
       +----+ +----+ +-------------+
       | HE | | Mac| | future host |
       | ND | |    | |             |
       | RIK| |    | |             |
       +----+ +----+ +-------------+
       copilot-agent (.NET 9)
            |
            | speaks ACP (JSON-RPC) over TCP loopback
            v
       +-----------------------+
       | copilot --acp         |
       |   --port <ephemeral>  |
       | (one per host, multi- |
       |  session)             |
       +-----------------------+

                  FCM v1 / Web Push (VAPID)
   +-------------------+  +-----------------+
   | Pixel             |  | Browser         |
   +-------------------+  +-----------------+
            ^                    ^
            +---- copilot-hub ---+
```

---

## Component 1 — `copilot-agent` (per-host daemon)

**Unchanged from v4.** Small .NET 9 daemon that wraps a single
`copilot --acp --port N` ACP child, scans `~/.copilot/session-state/`
for live-orphan and past sessions, exposes a tiny HTTP+SSE API to the
hub. See v4 §1 for full detail (bootstrap, enumeration, takeover,
HTTP API, discovery, auth, crash resilience, approval handling).

---

## Component 2 — `copilot-hub` (central daemon, on docker LXC)

### 2.1 Responsibilities (additions for v5 in **bold**)

1. Address book of agents; LAN UDP discovery; sqlite cache.
2. Phone & web-facing HTTP/SSE API (single API, two clients).
3. SSE proxy from agents to clients with fan-out (multiple clients
   per session).
4. **GitHub OAuth flow** for the web client: redirect to GitHub,
   exchange code for token, verify the GitHub user is on the
   allowlist (`["chsienki"]` for v1), issue a HttpOnly Secure cookie
   session bound to that identity.
5. **Static file serving** for the Blazor WASM bundle at `/`. Cookie
   auth gates everything except `/login`, `/oauth/callback`, and
   static assets.
6. **Bearer auth path** for the phone (unchanged from v4): phone
   POSTs `Authorization: Bearer <token>` and is treated as the same
   logged-in identity. No OAuth dance on phone.
7. **Web Push (VAPID)** sender: clients (browser tabs) register a
   PushSubscription on first visit; hub stores it next to FCM tokens.
   When notifying, hub fans out to BOTH the phone (via FCM) and any
   active subscribed browser sessions (via Web Push), de-duplicating.
8. SQLite caches: agent address book, session metadata mirror,
   FCM/Web Push subscriptions, OAuth state nonces.

### 2.2 Phone & web-facing API (unified)

Same JSON shapes, same endpoints, same SSE schema for both clients.
Auth is either `Cookie` (web) or `Authorization: Bearer` (phone).

```
GET    /api/agents                                         address book + online status
POST   /api/agents                                         manual add
DELETE /api/agents/:name                                   remove
POST   /api/agents/discover                                trigger LAN broadcast
GET    /api/agents/:name/sessions                          proxied
POST   /api/agents/:name/sessions                          new chat
GET    /api/agents/:name/sessions/:id                      details
GET    /api/agents/:name/sessions/:id/history              past events (no agent process spin-up)
POST   /api/agents/:name/sessions/:id/attach               adopt or resume
POST   /api/agents/:name/sessions/:id/detach               session/close, leave on disk
DELETE /api/agents/:name/sessions/:id                      kill + delete
POST   /api/agents/:name/sessions/:id/messages             prompt
GET    /api/agents/:name/sessions/:id/stream               SSE
POST   /api/agents/:name/sessions/:id/approvals/:aid       resolve permission request
POST   /api/agents/:name/sessions/:id/interrupt            cancel
POST   /api/devices                                        register FCM (phone) or PushSubscription (browser)
GET    /login                                              kicks off GitHub OAuth flow (web only)
GET    /oauth/callback                                     completes flow, sets cookie
POST   /logout                                             clears cookie / revokes subscription
```

### 2.3 Why static-served Blazor WASM (not Blazor Server)

| | WASM | Server |
|---|---|---|
| Offline tolerance | Some chat scrollback works offline | Useless without network |
| Hub CPU cost | Almost zero | Holds a SignalR circuit per tab |
| Cold-start latency | First load ~MB; cached after | Instant after first paint |
| Mobile background reconnect | Survives sleep/wake easily | SignalR circuit drops, full re-init |

Pick WASM. Cold-start hit is one-time and the hub has to do exactly
the same proxying work either way.

### 2.4 Deployment on docker LXC 102

- Adds `copilot-hub` service to `/srv/openclaw/docker-compose.yml`:
  ```yaml
  copilot-hub:
    image: ghcr.io/chsienki/copilot-hub:latest
    container_name: copilot-hub
    restart: unless-stopped
    network_mode: host             # for UDP broadcast discovery
    volumes:
      - ./hub-data:/data
    environment:
      HUB_PORT: 8443
      OAUTH_ALLOWED_GITHUB_USERS: chsienki
      OAUTH_CLIENT_ID: <ask user>
      OAUTH_CLIENT_SECRET: <ask user>
      VAPID_PUBLIC_KEY: <ask user>
      VAPID_PRIVATE_KEY: <ask user>
      FCM_SA_FILE: /data/fcm-service-account.json
  ```
- NPM proxy host added: `magpilot.home.sienkiewi.cz` → `192.168.1.239:8443`
  with the existing wildcard Let's Encrypt cert.

---

## Component 3 — `Magpilot.UI` (shared Blazor library)

**Stack:** Razor components, CommunityToolkit.Mvvm-style view models
in plain C# (no Blazor-specific MVVM lib needed; `INotifyPropertyChanged`
+ `StateHasChanged` is enough).

### 3.1 Components (used by BOTH MAUI and web shells)

- `<HostsList>` — discovered + manually-added agents, with online dot.
- `<SessionsTree>` — per-host expandable list, three categories
  (Live-Owned, Live-Orphan, Past); pull-to-refresh; "+ New chat".
- `<ChatView>` — markdown messages, inline tool-call cards,
  jump-to-bottom, copy-message.
- `<ApprovalModal>` — full-screen on small viewports, dialog on wide;
  haptic on phone, sound on both.
- `<OrphanAdoptModal>` — confirms PID/cwd/cmdline before kill+take-over.
- `<PastSessionView>` — read-only history rendered from
  `events.jsonl`, with a "Resume" button.
- `<SettingsPanel>` — server URL (web: locked to current origin;
  phone: editable), notifications toggle, theme, debug log export.

### 3.2 Layout

- Single-pane on narrow (phone): hosts → sessions → chat as a stack.
- Two-pane on tablet/desktop: sessions list left, chat right.
- Three-pane on wide desktop (>1400px): hosts left, sessions middle,
  chat right.

### 3.3 Cross-shell abstractions

The Blazor lib defines interfaces; shells implement them:

| Interface | MAUI shell impl | Web shell impl |
|---|---|---|
| `IAuthProvider` | Reads bearer from secure storage | Reads cookie identity from `/api/me` |
| `IPushProvider` | FCM token registration | Web Push subscribe via service worker |
| `IClipboard` | MAUI Essentials | `navigator.clipboard` |
| `IHapticFeedback` | MAUI Vibration | `navigator.vibrate` (no-op on desktop) |
| `IFileSaver` | MAUI FilePicker | `<a download>` + Blob |

---

## Component 4 — Phone shell (`CopilotChat.Maui`)

A thin .NET MAUI Blazor Hybrid app:

- One `BlazorWebView` hosting `Magpilot.UI` components.
- Implements the cross-shell interfaces (FCM, Keystore, etc.).
- App-launch flow: paste hub URL + bearer once → SecureStorage →
  Blazor UI takes over.
- Sideloaded APK via Obtainium from the GitHub Releases page.

---

## Component 5 — Web shell (`Magpilot.Web`)

A Blazor WebAssembly project that:

- References `Magpilot.UI` and renders the same components.
- Implements the cross-shell interfaces using web APIs.
- Service worker for offline scrollback + Web Push receive.
- GitHub OAuth flow handled by hub; client just reads `/api/me`
  and reacts to 401s with a redirect to `/login`.
- Bundle is published into the hub's `wwwroot/` and served as
  static files.

---

## Component 6 — Push notifications

```
copilot-hub --(HTTPS to fcm.googleapis.com)--> Pixel
copilot-hub --(HTTPS to mozilla/google push endpoints, VAPID-signed)--> Browser
```

Hub keeps a single `subscriptions` table:

```sql
CREATE TABLE subscriptions (
   id           TEXT PRIMARY KEY,
   identity     TEXT NOT NULL,        -- always 'chsienki' in v1
   kind         TEXT NOT NULL,        -- 'fcm' | 'webpush'
   endpoint     TEXT NOT NULL,
   keys_json    TEXT,                 -- p256dh / auth for webpush; null for fcm
   user_agent   TEXT,
   last_seen    INTEGER
);
```

Notification fan-out: query all subscriptions for the identity →
deliver via the right channel → drop subscriptions on `410 Gone` /
`unregistered` responses.

---

## Component 7 — Networking & security

- **Phone↔hub:** TLS via NPM cert, bearer auth.
- **Web↔hub:** TLS via NPM cert, GitHub OAuth → HttpOnly Secure
  SameSite=Lax cookie. CSRF token on all POSTs.
- **Hub↔agents:** TLS + per-agent bearer; LAN only.
- **ACP child:** loopback-only TCP.
- **OAuth allowlist** enforced server-side; if a non-allowed GitHub
  user logs in, hub returns 403 immediately. No way to set the
  allowlist from the UI; lives in env / config file.

---

## Component 8 — Operations & install

| Component | Where | Install |
|---|---|---|
| `copilot-hub` | docker LXC 102 | Add to `/srv/openclaw/docker-compose.yml`, `docker compose up -d copilot-hub`. Watchtower auto-updates. |
| `copilot-agent` Windows | HENDRIK | `copilot-agent install --service` |
| `copilot-agent` macOS | Mac | `copilot-agent install --service` (launchd) |
| `Magpilot.Web` SPA | inside hub container | Built into hub image; served at `/` |
| `CopilotChat.Maui` APK | Pixel | Obtainium → GitHub Releases |

NPM proxy host `magpilot.home.sienkiewi.cz` → `192.168.1.239:8443`,
with the existing wildcard cert.

---

## Open questions / decisions to revisit

1. **ACP id compatibility.** Are ACP `sessionId`s the same as on-disk
   `~/.copilot/session-state/<id>/` UUIDs? Spike A confirms.
2. **MAUI Blazor Hybrid Android JS perf.** The Pixel's WebView is
   fast, but verify chat scrollback with thousands of messages stays
   smooth. Mitigation: virtualize the message list (already standard
   in the Blazor lib).
3. **GitHub OAuth callback URL behind NPM.** Need to register
   `https://magpilot.home.sienkiewi.cz/oauth/callback` on a personal
   GitHub OAuth App. NPM passes through the path; hub handles it.
4. **Web Push from a self-hosted home service.** VAPID is straight-
   forward but browser push endpoints (mozilla, google) need internet
   reachable from the hub — which it has.
5. **Cookie + Service Worker + scope.** The web app's service worker
   must be scoped to `/` to receive push for `/api/...` traffic.
   Standard.
6. **Should the web client also support multiple identities later?**
   Out of scope for v1 (always Chris); but the cookie session model
   is identity-aware, so no rework needed when this happens.
7. **MCP servers in `session/new` / `session/load` calls.** Default
   to user's `~/.copilot/mcp-config.json` on the agent host.
8. **Concurrent clients on the same session.** A phone tab and a
   browser tab both attached: hub fan-out on SSE; ACP itself stays
   single-client per session via the agent multiplexer.
9. **MAUI iOS / Mac later.** Same MAUI Blazor Hybrid project, no
   per-platform UI rewrite.

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

1. **Spike A — ACP smoke test** (single biggest risk-retirement).
   Spawn `copilot --acp --port N`, do `initialize` +
   `session/new` + `session/prompt`. Then start a session in a
   regular terminal, kill it, `session/load` against its id from the
   ACP server. Confirm id compatibility.
2. **`copilot-agent` v0.** Single host, eight API endpoints, no
   auth, no TLS. Test from `curl`.
3. **`copilot-hub` v0.** Pure proxy + agent address book.
4. **`Magpilot.UI` v0.** Sessions tree + chat + approvals as Razor
   components, in a standalone Blazor WASM test harness pointed at
   the hub.
5. **Web shell.** Wrap UI in `Magpilot.Web`, add GitHub OAuth +
   cookie auth on the hub, serve static.
6. **MAUI shell.** Wrap the same UI in `CopilotChat.Maui` with
   Android FCM and SecureStorage.
7. **Multi-agent + LAN UDP discovery.**
8. **Orphan adoption + past-session resume + history rendering.**
9. **TLS for hub↔agents (TOFU pinning on hub side).**
10. **FCM + Web Push from the hub.**
11. **NPM proxy host + Watchtower-friendly compose service.**
12. **iOS target** (free with MAUI Blazor Hybrid). TestFlight.

Each step shippable independently.
