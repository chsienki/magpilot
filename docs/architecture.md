# Architecture and API

Magpilot connects browsers and local terminals to Copilot sessions on real
hosts. The hub is the browser's front door; agents run Copilot and keep session
state on their own machines. A terminal launcher coordinates exclusive
ownership with the local agent.

For installation and networking, use the [deployment guide](../deploy/README.md).
For implementation rules and the source-development loop, use
[AGENTS.md](../AGENTS.md). [Design notes](plan.md) explain the trade-offs and
remaining limits.

## Components

```text
Browser -- HTTPS --> Hub -- HTTP + SSE --> Agent -- stdio --> Copilot runtime
                                           ^
                                           |
Terminal --> magpilot --> installed Copilot CLI
                   local ownership protocol

External clients -------------------------> Agent HTTP API
```

| Project | Responsibility | Target |
|---|---|---|
| `Magpilot.Hub` | Agent registry, authentication, proxying, central logs, web hosting | ASP.NET Core 9 |
| `Magpilot.Agent` | SDK sessions, history, permissions, ownership, discovery | ASP.NET Core 9 |
| `Magpilot.Host` | The `magpilot` terminal launcher, pairing, Windows updates | .NET 10, Windows x64 release |
| `Magpilot.Shared` | HTTP DTOs, SSE events, versioning | .NET 9 |
| `Magpilot.UI` | Shared Razor components and browser API clients | .NET 9 |
| `Magpilot.Web` | Blazor WebAssembly shell, published into the hub | .NET 9 |

The agent bundles the public .NET Copilot SDK's out-of-process runtime. The
launcher uses a separately installed, authenticated `copilot` executable; it
does not replace that executable on `PATH`.

**Satellites know about Magpilot; Magpilot does not know about satellites.**
Product-specific routing, schedules, messaging integrations, and provisioning
belong in external HTTP clients or deployment bootstrap hooks. Shared UI
consumers may reference `Magpilot.UI`, but the platform must not reference them.

## Runtime and session configuration

`IAgentSessionRuntime` separates HTTP and ownership orchestration from SDK
types. `SdkSessionRuntime` implements create/resume, serialized prompting,
abort, detach, stream publication, and configuration verification.

`SdkClientPool` starts no Copilot process at agent startup. It lazily creates
SDK clients using `CopilotClientMode.CopilotCli` and stdio transport. Distinct
`CopilotHome` values produce distinct clients; model, reasoning, working
directory, selected agent, tools, and MCP restrictions are session options.

Session creation and adoption accept:

| Field | Meaning |
|---|---|
| `model`, `reasoningEffort` | Runtime model selection and supported reasoning level |
| `fusionTier` | Fusion orchestration tier (`standard`, `high`, or `max`) with model `auto` |
| `agent` | A selected custom agent definition |
| `availableTools` | Model-facing tool allowlist |
| `disableMcpServers` | Explicit SDK server exclusions |
| `noCustomInstructions` | Suppress ambient instructions |
| `copilotHome` | Isolated configuration root |

Creation also accepts `cwd`, `name`, and an optional `initialPrompt`. The exact
request shapes are in [Requests.cs](../src/Magpilot.Shared/Models/Requests.cs).

Copilot Fusion selects models and execution patterns itself. Configure it with
`"model": "auto", "fusionTier": "high"`, without `reasoningEffort`.
The stable SDK's Auto-tier routing API applies this preference on creation,
resume, and idle model updates. Runtime status reports `fusionTier` separately
from a backing model's reasoning, including a confirmed pending tier before
its next turn. The effective profile and terminal lease retain that selection.
The browser status row labels Fusion explicitly; tier selection is available
through API/profile configuration rather than a dedicated browser picker.

Custom homes must already exist and link their `session-state` entry to the
agent's canonical Copilot session store. `SdkCustomAgentLoader` resolves a
selected `<name>.agent.md` from that home's `agents` directory. Without a custom
home, it searches the user's `.copilot/agents` before the working directory's
`.github/agents`. It parses the definition into the SDK's explicit
`CustomAgents` configuration; setting a name alone is insufficient.

Tool filtering does not prevent an MCP server from starting. Restricted
profiles must also exclude unwanted servers explicitly, including
`github-mcp-server` when the built-in GitHub server is inappropriate.

An attached session can change model and reasoning while idle. Changes to
agent, tools, instruction scope, or configuration home require a fresh
attachment. Configuration and prompting share a per-session gate: a session
whose requested configuration cannot be applied and verified is quarantined,
not advertised as ready to prompt.

## Session state and ownership

History is stored under the host user's `.copilot/session-state`. Detaching
stops the runtime attachment; it does not delete the persisted conversation.

The session list uses three numeric `SessionState` values:

| Value | Name | Meaning |
|---|---|---|
| `0` | `Owned` | Attached to this agent |
| `1` | `Locked` | A live holder exists outside this agent's ownership |
| `2` | `Dormant` | No live holder |

Use `GET /state` for the richer ownership decision:

| `owner` | Meaning |
|---|---|
| `None` | No current driver |
| `Agent` | This agent's runtime is driving |
| `Host` | A launcher holds a terminal lease |
| `External` | A live process is not coordinated by this agent |
| `Contended` | Independently controlled live holders can write the same session |

The response includes `info`, `hostPid`, `hostLeaseId`, `activity`, `inFlight`,
`lastEvent`, `runtimeStatus`, and `foreignHolderPids`. Ownership and activity
are string enums. Runtime status is nullable and contains cached model,
reasoning, context usage, and session AI Credits; unavailable values are not
zero. `/state` does not make SDK RPCs.

Copilot's `inuse.<pid>.lock` files are advisory, not a mutex. An SDK session
may acquire its runtime lock after attachment, so the agent recognizes its
own direct-child SDK process (`copilot-runtime` or Windows' `copilot` wrapper)
even when no lock existed during the initial scan. Other live holders remain
foreign. The agent persists terminal
leases in `.copilot/magpilot/hostownership.json`. PID start times guard against
PID reuse. Startup reconciliation can recover launcher ownership from process
ancestry, but must not reclassify resident SDK sessions as terminal-owned.

### Terminal handoff

1. A launcher with an explicit target requests release, then calls
   `/acquire-for-host`. A polite acquisition waits for the current agent turn;
   an explicit force request aborts it and requires a clean boundary.
2. The agent blocks new prompts, detaches the runtime, records the effective
   session profile, and returns a terminal lease.
3. The launcher runs Copilot. When another client requests release, it stops
   that child before submitting `/release` with the exact lease ID.
4. The agent reloads disk state and restores the profile. It clears terminal
   ownership only after configuration succeeds and no foreign writer remains.

A stale lease cannot release a newer acquisition. Normal release never kills
foreign holders; explicit `/take-over` is the agent-side force transition.
Failed restoration retains recovery information rather than pretending the
session is agent-owned.

Automatic startup and interactive session-switch detection are different from
explicit user-selected targets: **acquire first, notify second**. A guessed
target must not preempt another launcher before atomic acquisition succeeds.
`AutomaticHostAcquisition` enforces this ordering.

Bare `magpilot --resume` selects a session before Copilot starts. Interactive
CLI `/resume` can transfer the lease when a new or refreshed child-PID lock
identifies the target. An artifact timestamp or `alreadyInUse` event alone
cannot identify its writer; a CLI switch without a child lock may remain
uncoordinated. The watcher removes only that child's stale old-session lock,
serializes handback, and reconnects its subscription to the selected session.

### Browser handoff and concurrent clients

The browser checks ownership before streaming and during stream-health,
visibility, and refresh reconciliation. Host or contended ownership pauses the
composer. **Take back** requests a graceful release first; failure exposes a
separate **Force take over** action instead of silently killing the terminal.

A prompt rejected with `409` and `needsRelease` requires release negotiation:
request release, poll `/state` until ownership is available, then retry. A
`409` with `needsReadopt` requires adoption/configuration recovery instead.
Do not treat every conflict as permission to force a takeover.

## Agent HTTP API

Routes below are relative to `/api` on the agent, normally TCP 5099.
They require `Authorization: Bearer <agent-token>` except the three version
GETs. `/healthz` is also unauthenticated.

| Method | Route | Purpose |
|---|---|---|
| GET | `/version` | Running version and protocol |
| GET | `/version/latest?from=<version>` | Cached latest release, compared with the caller |
| GET | `/version/status` | Running/latest composite status |
| POST | `/version/refresh` | Refresh release metadata from the configured hub |
| GET | `/info` | Host identity, OS, and working directory |
| GET | `/sessions` | Sessions, newest activity first |
| GET | `/sessions/{id}` | Session metadata |
| GET | `/sessions/{id}/history` | Persisted, paged history without attaching |
| POST | `/sessions` | Create a configured session |
| POST | `/sessions/{id}/adopt` | Attach/resume and configure |
| POST | `/sessions/{id}/detach` | Detach; `?force=true` permits runtime recovery |
| POST | `/sessions/{id}/messages` | Admit a prompt; returns `202` |
| POST | `/sessions/{id}/interrupt` | Abort an agent turn |
| POST | `/sessions/{id}/approvals/{approvalId}` | Resolve permission with `optionId` |
| GET | `/sessions/{id}/stream` | SSE subscription; optional `load` and `force` |
| GET | `/sessions/{id}/state` | Authoritative ownership and cached runtime status |
| GET | `/sessions/{id}/model-options` | Runtime-advertised model/reasoning choices |
| POST | `/sessions/{id}/model` | Apply model and reasoning while agent-owned and idle |
| POST | `/sessions/{id}/yolo` | Set per-session automatic approval |
| POST | `/sessions/{id}/release-request` | Broadcast a cooperative release request |
| POST | `/sessions/{id}/acquire-for-host` | Local launcher acquisition |
| POST | `/sessions/{id}/release` | Local launcher handback by lease |
| POST | `/sessions/{id}/take-over` | Acquire for the agent, optionally with explicit force |
| POST | `/quick-prompt` | Wait for a complete response as one JSON result |

There is no session-delete endpoint or interactive slash-command endpoint.
The source of truth is
[AgentEndpoints.cs](../src/Magpilot.Agent/Api/AgentEndpoints.cs).

### Prompts, history, and long-lived sessions

Subscribe before sending `/messages`. Its `202` response confirms admission,
not completion; wait for `turn_complete` before clearing busy state. A source
label such as `"source": "external-client"` prefixes the model prompt with
`[via external-client]` and emits a `user_delta` for other watchers. Ordinary
browser sends omit the source and use local echo.

`/messages` requires an already attached, non-quarantined session. For an
external client's stable conversation, create a named session, retain its ID,
and adopt it after agent restart before using `/messages`.

`/quick-prompt` takes `prompt`, optional `cwd`, `timeoutSeconds` (default 60),
`keepSession`, `sessionId`, and `source`. It returns `responseText`,
`stopReason`, and `sessionId`. Without an ID it creates an ephemeral session
and detaches after the turn unless `keepSession` is true. With an ID it adopts
on demand and leaves that session attached. Detachment never removes history.
This convenience route does not accept the full session-profile options; use
creation/adoption to establish a specialized profile first.

History returns `{ entries, oldestCursor, hasMore }`. Use `?tail=50` for the
initial page and `?before=<cursor>&limit=50` for older entries. `?all=true`
requests the full history and should be used sparingly. The hub preserves
these query parameters; clients reject non-advancing cursors.

### Streaming and permissions

SSE frames contain one JSON object per `data:` record. The discriminator is
lowercase `type`, while event payload properties retain their C# casing:

```text
data: {"type":"assistant_delta","Text":"Hello"}

data: {"type":"turn_complete","StopReason":"end_turn"}
```

HTTP request/response DTOs use ASP.NET's web JSON defaults; do not assume those
defaults also apply to SSE. Event definitions are in
[StreamEvent.cs](../src/Magpilot.Shared/Models/StreamEvent.cs).

| Events | Meaning |
|---|---|
| `assistant_delta`, `thought_delta`, `user_delta` | Incremental conversation content |
| `tool_call_start`, `tool_call_progress`, `tool_call_end` | Tool lifecycle, keyed by tool-call ID |
| `approval_required` | Permission request with selectable options |
| `turn_complete`, `error` | Turn boundary and error information |
| `load_started`, `history_done`, `load_failed` | Optional load-on-connect lifecycle |
| `release_requested` | Cooperative ownership notification |
| `heartbeat` | Connection keepalive, every 15 seconds |

The SDK mapper publishes streaming deltas without duplicate final messages.
If a message arrives only as a completed canonical message, as Fusion can
produce, it publishes that content once instead of dropping the response.
`session.idle` is the successful turn boundary. Errors publish one error and
one terminal event; later idle events must not double-complete the turn.
The stall watchdog aborts silent turns after its configured threshold, but
exempts open tool calls.

The browser renders permissions inline with **Allow once** and **Deny**
buttons. Per-session YOLO and agent-wide automatic approval apply only to
ordinary requests; managed-policy approvals always require a user. Pending
requests fail closed on cancellation or after five minutes. Enabling YOLO
also releases ordinary requests already waiting.

## Hub API and authentication

The browser reaches agent routes through
`/api/agents/{name}/sessions/...` and `/api/agents/{name}/quick-prompt`.
Terminal `/acquire-for-host` and `/release` remain local agent operations, not
hub proxies. The hub does not proxy `/info` or every agent version route.

Additional hub routes:

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/me` | Signed-in identity and administrator flag |
| GET | `/api/agents` | Caller-scoped agent list |
| GET | `/api/admin/agents/all` | Administrator's cross-user agent list |
| DELETE | `/api/agents/{name}` | Remove a registry entry |
| POST | `/api/agents/discover` | Trigger a discovery sweep |
| POST | `/api/admin/agents/{name}/revoke` | Revoke agent credentials |
| GET | `/api/agent-version` | Hub's cached latest release |
| GET | `/api/agents/version-status` | Aggregate running/latest versions |
| POST | `/api/agents/check-updates` | Refresh release metadata and signal agents |
| POST | `/api/admin/enroll/voucher` | Issue an enrollment bundle |
| POST | `/api/enroll/redeem` | Redeem a single-use voucher without an existing login |
| POST | `/api/enroll/claim` | Submit an interactive pairing claim |
| GET | `/api/enroll/claim-status` | Long-poll using the claim secret |
| GET | `/api/admin/agents/claims` | Pairing requests |
| POST | `/api/admin/agents/claims/{id}/approve` | Adopt a pending request |
| POST | `/api/admin/agents/claims/{id}/reject` | Reject a pending request |
| POST | `/api/log`, `/api/log/batch` | Central log ingestion |
| GET | `/api/log`, `/api/log/sources` | Administrator-only log queries |
| POST | `/api/devices` | Accepted stub only; no subscription persistence or push delivery |

`GET /login` starts GitHub OAuth, `GET /oauth/callback` completes it, and
`POST /logout` clears the cookie. Static SPA assets and `/healthz` are public;
the API, not the static files, enforces access control.

### Trust boundaries

- **Browser users** authenticate with GitHub and an HttpOnly, SameSite=Lax
  cookie. `OAUTH_ALLOWED_GITHUB_USERS` is the allowlist; its first entry is the
  administrator. Normal agent lists are owner-scoped even for that admin.
  The admin can use the separate all-agents view and manage any agent.
- **Infrastructure callers** use `MAGPILOT_HUB_BEARER`. This credential is
  unscoped and can reach every agent; it is not a per-user or read-only token.
- **Agent API callers** use that agent's `MAGPILOT_AGENT_TOKEN`. The hub stores
  each token in its registry. A shared agent-token environment variable on the
  hub is not used.

Agent ownership is stamped by voucher redemption or claim approval. Null-owner
rows belong to the administrator's normal view. A single endpoint filter gates
hub routes containing `{name}` and returns `404` to callers without access.
Revoked proxy calls return `410`; transport and agent-token failures surface
as `502` rather than sending the browser through a hub-login loop.

Pending interactive claims are **not owner-scoped**: their list and
approve/reject routes require authentication, not administrator status. Any
allowlisted user can adopt a pending claim. Restrict deployments to mutually
trusted users, verify fingerprints, and use identity-bound enrollment bundles
for predictable ownership. This is not a hostile multi-tenant boundary.

### Pairing and discovery

Agents answer discovery on UDP 47823; the hub probes about every 60 seconds.
The separate hub pairing responder listens on UDP 47824. Discovery updates
location and online state, not credentials or ownership. Broadcasts stay within
their network segment and may be filtered by access points or container
networking.

Interactive pairing uses a five-minute claim and a six-character fingerprint.
The launcher polls until approved, then writes the hub URL and minted agent
token. It does **not** receive a hub bearer from this flow.

Enrollment bundles use `magpilot2+<base64url(JSON)>` with `hubUrl`, `voucher`,
and `hubBearer`. The voucher is single-use and expires after 15 minutes.
Redemption mints the agent token; that token is not embedded in the bundle.
The bundle is sensitive because it contains the infrastructure bearer.
See the [installer guide](../installer/README.md#pairing).

## Persistence, logging, and transport

The hub's data directory holds `hub.db` (registry and enrollment state) and
`logs.db` (central logs with FTS5 search). The log store is capped at 50,000
rows and trims every five minutes. Agent Warning-and-higher logs are forwarded
only when both hub URL and bearer are configured; browser logging also feeds
the hub. Bounded producer queues drop oldest entries instead of growing
without limit.

Hub-to-agent HTTP clients have distinct budgets:

| Kind | Default | Use |
|---|---|---|
| `Read` | 10 seconds | Fast control-plane calls and prompt admission |
| `Action` | 90 seconds | Create, adopt, detach, model operations, takeover |
| `Stream` | Infinite | SSE and synchronous quick prompts |

The launcher similarly separates 15-second control calls, 90-second ownership
actions, and unlimited SSE. A timeout is not equivalent to a caller-cancelled
subscription.

Hub-to-agent traffic is HTTP with bearer authentication. HTTPS termination,
trusted forwarding proxies, firewalls, VPN routing, persistent storage, and
backups are deployment responsibilities. Reverse proxies must disable SSE
buffering and allow long reads; see [deployment](../deploy/README.md).

## Browser UI and host reuse

`Magpilot.Web` hosts the MudBlazor-based `Magpilot.UI` Razor library. Its drawer
switches between a host picker and that host's sessions. Chat renders Markdown,
optional reasoning, tool-call chips, inline approvals, a draft composer, and a
per-session message queue. Drafts and queues survive navigation and refresh
through local storage. Older history is demand-loaded.

The composer status row shows model/reasoning, context percentage, and session
AI Credits. Model choices come from the runtime, not a hard-coded catalog.
Editing requires an online, agent-owned, idle session. The toolbar's
**Show session info** setting controls the row's visibility.

`Home.razor` owns connection orchestration and per-session caches. It cancels
stale stream generations and reconnects after transient mobile-tab network
drops. An attached session without an in-tab cache loads persisted history
before joining the live stream.

Hosts reusing `ChatView` must provide its app-root JavaScript modules
(`js/composer.js` and `js/history-scroll.js`) and the required UI services.
Use its generic `OverlayContent` and `ComposerLeadingContent` slots for
host-specific controls. Theme state and the `MagpieMark` component remain
shared; the platform must not know which product embeds them.
