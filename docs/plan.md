# Design notes and open work

These notes explain the choices behind the shipped system. The
[architecture guide](architecture.md) is the current API reference;
[deployment](../deploy/README.md) describes how to run it. This is not an
implementation checklist or a promise that every idea below will ship.

## The problem

A Copilot conversation belongs to a project on a real machine, but its user
should not have to stay at that machine's terminal. Magpilot gives browsers
and terminals access to the same persisted sessions without moving the
repository, credentials, or tools to a hosted service.

The useful unit is a session on a host, not a browser tab. Losing a tab must
not delete the conversation, and switching surfaces must not create concurrent
writers with divergent context.

## Why a hub and per-host agents?

The hub provides one browser address, authentication, an agent registry, and
central observability. Agents keep filesystem access, runtime processes, and
session ownership close to the work.

This is deliberately not a scheduler, messaging gateway, or personal-assistant
product. Those concerns can vary independently as external API consumers.
Bootstrap hooks provision a deployment without teaching the platform its
product names or secrets.

The trade-off is operational: the hub must reach every agent, and LAN
broadcast discovery cannot solve routing across a VPN or segmented network.
Pairing establishes trust; discovery only finds addresses.

## Why the Copilot SDK?

The public .NET SDK supplies typed session lifecycle, structured streaming,
permission callbacks, model capabilities, and usage information. Its bundled
out-of-process runtime avoids depending on an independently updated npm CLI
for agent-hosted chats.

The terminal still uses the user's installed Copilot CLI. Sharing persisted
state does not make its interactive TUI commands SDK operations: the browser
needs explicit controls for supported actions.

Configuration isolation is a runtime boundary, not just prompt wording.
Separate Copilot homes, tool allowlists, MCP exclusions, custom agents, and
instruction policy allow external products to create constrained profiles.
Unverified configuration must fail closed instead of silently answering with
the wrong model or tool access.

## Why Blazor WebAssembly and a shared UI library?

The hub can serve a static browser bundle without a server-side UI circuit
per tab. A mobile browser can reconnect to HTTP/SSE after backgrounding while
keeping client-side presentation state.

`Magpilot.UI` separates reusable chat components from the web shell. Other
hosts can embed those components without making the platform depend on them.
This does not imply a native Magpilot phone app exists.

Browser state is a convenience cache, not the session authority. Durable
history stays on the agent host; drafts and queued messages are local to the
browser.

## Why explicit terminal leases?

Copilot's lock files describe process activity but do not prevent multiple
writers. A launcher must coordinate ownership with the agent rather than
simply launch a second process against the same history.

Lease IDs reject stale handbacks. Recording the effective runtime profile
preserves model, agent, and isolation settings across a terminal round trip.
Graceful handoff is preferred; killing another process requires an explicit
force operation.

Session detection remains constrained by the CLI's observable behavior.
Pre-launch selection makes bare `--resume` deterministic. Interactive
`/resume` can be coordinated only when a child-process lock identifies the new
session; unrelated file activity is not sufficient evidence.

## Shipped capabilities and remaining limits

| Area | Shipped | Remaining work or constraint |
|---|---|---|
| Clients | Responsive browser UI and Windows terminal launcher | No standalone Magpilot Android or iOS app |
| Sessions | Persistent history, paging, named sessions, explicit ownership and handoff | Interactive resume detection depends on CLI lock updates |
| Permissions | Inline approvals, per-session YOLO, managed-policy approval handling | No dedicated approval-modal experience |
| Session controls | Model/reasoning changes and runtime usage status | Broader tool, MCP, instruction, and agent-profile editing is not exposed in the SPA |
| Notifications | `/api/devices` accepts a request as a stub | No persistence or FCM/Web Push delivery |
| Identity | GitHub allowlist and owner-scoped browser agent access | Pending pairing claims are shared among authenticated users; infrastructure bearer is unscoped |
| Transport | HTTP/SSE and bearer authentication to agents | No built-in hub-to-agent TLS; use trusted networks or external transport protection |
| Packaging | Windows x64 installer; Linux amd64 hub and agent images | No equivalent packaged macOS/Linux launcher installer |
| Updates | Published release discovery and explicit Windows updater | No remote installation from the hub; no coordinated restart of all running terminal sessions |
| Protocol | Shared HTTP/SSE contracts and version reporting | Protocol 0 requires components to update together |
| UI maintenance | Shared components, history paging, reconnect handling | Session orchestration remains concentrated in a large `Home.razor` |

There is no context-clear API, session-delete API, or full session listing from
`--magpilot-status`. Do not infer implemented behavior from a flag name, a stub,
or an early proposal.

## Evaluating future changes

Keep additions generic and put policy at the right boundary. API changes need
matching agent, hub, shared-contract, and client changes. State transitions
must preserve single-writer ownership and verified configuration, including
failure and restart paths.

Runtime versions, package versions, and release automation belong to project
files and workflows rather than a second version catalog in prose. Capture
reproducible incidents and actionable follow-ups in public issues when they
are ready for development.
