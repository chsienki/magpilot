# Magpilot contributor and agent guide

Read this before editing. The [README](README.md) is the public introduction.
This guide covers the development loop and implementation invariants, not a
particular person's deployment.

| Guide | Owns |
|---|---|
| [Architecture and API](docs/architecture.md) | Runtime behavior, wire contracts, ownership, authentication, UI composition |
| [Deployment](deploy/README.md) | Configuration, networking, containers, backups, releases |
| [Windows installer](installer/README.md) | Installation, pairing, upgrades, packaging |
| [Design notes](docs/plan.md) | Rationale and honest limits, not an API specification |
| [Terminal tools](tools/tui-matrix/README.md) | Launcher styling, capture, probes, theme editing |

## Architectural law

**Satellites know about Magpilot. Magpilot does not know about satellites.**

Keep external products, hostnames, pinned session IDs, schedules, and
product-specific behavior out of the platform. Extend the public HTTP API or
use a deployment's bootstrap hooks instead of adding a special case.
Consumers may reference `Magpilot.UI`; the shared library must not depend on
those consumers.

Hook-specific settings belong in the generic
`MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST`, not in a platform list of product
variables. Its validation and privilege-drop rules are part of the
[deployment contract](deploy/README.md#bootstrap-hooks).

## Repository map

| Location | Responsibility |
|---|---|
| `Magpilot.slnx` | Solution; use `dotnet build` and `dotnet test`, not a nonexistent `.sln` |
| `src/Magpilot.Shared` | HTTP DTOs, stream events, release/protocol models |
| `src/Magpilot.Agent/Api` | Agent HTTP/SSE routes |
| `src/Magpilot.Agent/Runtime` | Generic runtime contract and session profiles |
| `src/Magpilot.Agent/Runtime/Sdk` | SDK client pool, runtime, events, permissions, custom agents, status |
| `src/Magpilot.Agent/Sessions` | Scanning, registry, history, terminal ownership |
| `src/Magpilot.Hub` | Agent registry, proxy, identity, enrollment, logs, release discovery |
| `src/Magpilot.Host` | `magpilot` launcher, PTY, session detection, pairing, updater |
| `src/Magpilot.UI` | Shared components, pages, API clients, theme |
| `src/Magpilot.Web` | Browser shell and app-root JavaScript |
| `tests` | Agent, Hub, and launcher xUnit test projects |
| `installer` | Inno Setup and scheduled-task/firewall helpers |
| `deploy` | Example hub Compose stack |
| `scripts` | Build, install, acceptance, and terminal diagnostics |
| `tools/tui-matrix` | Node-based capture rendering and offline theme lab |

The launcher and its tests target .NET 10. Agent, Hub, Shared, UI, Web, and
their other test projects target .NET 9. Use the .NET 10 SDK with .NET 9 and
ASP.NET Core 9 runtimes available. CI builds and tests on Windows.
Package and bundled-runtime versions live in the project files, not a
separate prose version catalog.

## Build, run, deploy

Use PowerShell 7 from the repository root:

```pwsh
dotnet build Magpilot.slnx -c Release
dotnet test Magpilot.slnx -c Release --no-build
.\scripts\build-hub.ps1
```

`build-hub.ps1` publishes the WASM app, replaces the generated
`src\Magpilot.Hub\wwwroot`, then publishes the hub. A plain solution build does
not perform that copy. Rebuild the bundle and restart the hub after UI edits.
Do not delete `Magpilot.Web/wwwroot`, which is source.

### Local end-to-end development

These examples bind to loopback and use development-only credentials. Do not
copy the auth bypass into a shared deployment.

If an installed `MagpilotAgent` task uses this host's ports, stop it before
starting the source agent and restart it when finished:

```pwsh
Stop-ScheduledTask -TaskName MagpilotAgent
```

Start the hub in one terminal after building:

```pwsh
$env:MAGPILOT_HUB_BEARER = "local-dev-bearer"
$env:MAGPILOT_HUB_PUBLIC_URL = "http://127.0.0.1:7088"
$env:MAGPILOT_HUB_DATA = Join-Path $env:TEMP "magpilot-hub-dev"
$env:OAUTH_ALLOWED_GITHUB_USERS = "dev-user"
$env:MAGPILOT_DEV_BYPASS_AUTH = "true"
$env:ASPNETCORE_URLS = "http://127.0.0.1:7088"
dotnet run --project .\src\Magpilot.Hub -c Release --no-launch-profile --no-build
```

Open `http://127.0.0.1:7088/dev-login`, then create
an enrollment bundle at `/admin/enroll`. Use a separate terminal for pairing
and the source agent:

```pwsh
$env:MAGPILOT_ENV_FILE = Join-Path $env:TEMP "magpilot-agent-dev.env"
dotnet run --project .\src\Magpilot.Host -c Release --no-build -- "--magpilot-pair=<bundle>"
$env:MAGPILOT_AGENT_PUBLIC_URL = "http://127.0.0.1:5099"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
dotnet run --project .\src\Magpilot.Agent -c Release --no-launch-profile --no-build
```

Replace `<bundle>` with the generated value. `MAGPILOT_ENV_FILE` isolates the
settings and prevents pairing from restarting an unrelated installed task.
The agent loads that file at startup; already-exported environment variables
win, so do not leave a different token in the shell. Use a fresh file/bundle
when switching development hubs.

This isolates connection settings, not conversation data: the source agent
still uses the current user's Copilot session store. Use disposable sessions
for development.

The bundle flow matters: discovery pairing supplies an agent token but does
not supply the hub bearer required by update polling and log forwarding.
The hub rejects enrollment-bundle creation with the literal default
`dev-bearer`; `local-dev-bearer` above is only a loopback development value.

Pass `--no-launch-profile` to services so launch settings cannot override
their advertised URLs. `dotnet watch --project .\src\Magpilot.Web` does not
automatically proxy through the hub. For an end-to-end browser change, rebuild
the SPA and use the hub; configure an explicit proxy for a separate dev server.

Verify readiness with the actual listener:

```pwsh
Invoke-RestMethod http://127.0.0.1:7088/healthz
Invoke-RestMethod http://127.0.0.1:5099/healthz
```

Stopping a parent shell can leave a launched service holding its port. Inspect
the listener's PID and start time before restarting; stop only the specific
process you intended to replace, never every `dotnet` process. Set `$processId`
to the identified service PID before using the last two commands:

```pwsh
Get-NetTCPConnection -LocalPort 5099,7088 -State Listen |
    Select-Object LocalAddress, LocalPort, OwningProcess
Get-Process -Id $processId | Select-Object Id, ProcessName, StartTime
Stop-Process -Id $processId
```

Restore the installed task when done:

```pwsh
Start-ScheduledTask -TaskName MagpilotAgent
```

For production configuration, image publishing, and release procedure, use
[deployment](deploy/README.md). Do not use the release/install loop for routine
local iteration.

## Runtime invariants

- `SdkClientPool` owns one lazy SDK client per configuration-home isolation
  key. Do not share clients across homes. The agent uses stdio, not
  experimental in-process hosting.
- Custom homes must link to the canonical scanned session store. Selected
  agent definitions must be loaded into explicit SDK `CustomAgents`; a name
  alone does not load a definition.
- Profiles include the complete working directory, instruction, agent, model,
  reasoning, tool, and MCP configuration. Tool allowlists do not prevent
  excluded MCP servers from authenticating or starting.
- Configuration, prompting, detach, and handoff share session/routing gates.
  Do not admit a turn between model and reasoning updates or on an unverified,
  quarantined route.
- `/messages` returns `202` after admission, not after completion. Busy state
  ends on `TurnComplete`, never on the HTTP response. It does not adopt a
  dormant session; `/quick-prompt` can adopt on demand.
- `SdkTurnEventMapper` publishes streaming message/reasoning deltas and ignores
  duplicate final messages. Final-only canonical messages, including Fusion
  commits, must be emitted once when no deltas exist for that message ID.
  A successful turn ends on SDK `session.idle`;
  errors publish exactly one error and terminal boundary.
- `SdkPermissionBroker` fails closed. Managed-required permissions always
  require a human. Ordinary pending requests must observe dynamic YOLO changes,
  including the registration-versus-toggle race.
- SDK runtime locks can appear after session attachment. In addition to
  holders captured at attach, recognize a live SDK process (`copilot-runtime`
  on Linux, `copilot` on Windows) that is a direct child of this agent;
  do not classify arbitrary descendants or external processes as SDK-owned.
  Never evict the agent's own runtime.
- The turn watchdog exempts open tool calls. Silence from a long-running tool
  is not proof of a stalled model.
- The SDK's unused in-process FFI alias is excluded from build/publish output.
  Do not re-enable it without an implementation using in-process hosting.
- Fusion uses `model=auto` plus `fusionTier` (`standard`, `high`, or `max`),
  not a fabricated model ID or fixed reasoning effort. The stable SDK maps
  these through its Auto-tier API. Verify authoritative Fusion/pending-tier
  metadata before marking the session ready, and preserve the tier through
  adoption, terminal leases, and status refreshes.

## Ownership and launcher invariants

- Lock files are not mutual exclusion. Use the registry's authoritative
  ownership state and terminal leases, including every live foreign holder.
- Acquisition drains prompt admission before waiting for a turn boundary.
  Handoff records the effective profile and restores it before advertising the
  session as agent-owned.
- The launcher must stop its Copilot child before releasing its exact lease.
  A stale lease must not affect a newer acquisition. Normal release never
  kills foreign holders; force takeover is explicit.
- Persist host ownership with PID start times. Reconciliation must skip
  resident SDK sessions even if a development agent descends from a launcher.
- Explicit target selection can notify before acquisition. Automatic startup
  or switch detection must acquire first, then notify; a guessed target must
  never preempt another launcher before it holds a lease.
- Bare `--resume` uses `ResumeSessionPicker` before spawning Copilot.
  Interactive switches need a new/refreshed child-PID lock. File activity and
  `alreadyInUse` events do not identify their writer.
- Serialize old-session handback with session transfer. Delete only the
  child's exact stale lock, retain failed leases for retry, and respawn the
  currently selected session after a browser round trip.
- Keep launcher's control/action/stream HTTP clients separate. Only a
  cancellation of the subscription's own token ends reconnect; HTTP timeout
  cancellation must enter retry/backoff instead.
- Windows PTY output must drain through EOF before disposal or terminal
  restoration. Input/resize cancellation must not truncate Copilot's final
  terminal-reset sequences. Keep `PtyOptions.UseAsyncIo=true`.
- The supported ConPTY path is Porta.Pty's app-local assets. In-box ConPTY is
  a diagnostic control, not a substitute with equivalent query behavior.

## Hub and API invariants

- Keep DTOs in `Magpilot.Shared`. HTTP JSON uses web defaults; SSE uses the
  default serializer with lowercase `type` but C# payload-property casing.
  Use the source-generated launcher JSON contexts for Native AOT; do not add
  reflection-based JSON calls to the launcher.
- Protocol 0 is intentionally unstable. Update agent, hub, launcher, and UI
  together; do not invent compatibility adapters or increment zero for each
  breaking change. A compatibility freeze must be an explicit decision.
- Use `AgentHttpClient`'s `Action` client for operations that can load/recycle
  runtimes, including detach and model operations. Fast control calls use
  `Read`; SSE and `/quick-prompt` use `Stream`.
- Per-agent policy uses the `/api` group's `{name}` endpoint filter and the
  `Proxy` helper. Keep revocation, online-state updates, and upstream-error
  translation centralized.
- Main agent-list scoping and permission to access an agent are different
  predicates. Administrator users have a separate all-agents view;
  infrastructure bearer callers remain unscoped.
- Claims are authenticated but not owner-scoped. Do not document them as
  administrator-only or imply hostile-user isolation.
- `AgentRegistry.AddColumnIfMissing` owns additive registry schema changes.
  Do not issue a bare, non-idempotent `ALTER TABLE` during startup.
- Use `MagpilotPairWriter` for atomic env-file updates and task restarts.
  Preserve unrelated settings; honor `MAGPILOT_ENV_FILE` isolation.
  Normal launcher reads use `InstallConfig`, not that pairing-output override;
  document the distinction rather than assuming both readers are identical.
- History proxies must preserve `tail`, `before`, and `limit`. UI paging must
  reject a non-advancing cursor.
- `UpdatedAt` comes from the newest `events.jsonl`/`workspace.yaml` mtime, not
  YAML's embedded `updated_at`. Preserve YAML block-scalar parsing for names.
- Version checks compare release metadata with the caller's version through
  `Versioning.IsUpdateAvailable`, not just the agent's version. Agent and
  launcher binaries can differ after a partial update.

## Browser and shared UI invariants

- `HubClient` and `HubLogClient` own credential-aware HTTP clients. There is
  no bare `HttpClient` registration in the WASM container.
- Cancel prior stream generations before starting another. Stale navigation
  responses must not update a different host/session. Treat transient
  browser-network EOF as reconnectable, not as an unhandled render failure.
- An attached session without an in-tab cache needs persisted history before
  live streaming. A cache restore must not resume an already attached runtime.
- Ownership reconciliation can pause the composer, but must not auto-unlock
  it during a launcher's acquisition window. Browser takeover stays graceful
  first, with force as a separate user action.
- Draft and queue persistence is per agent/session. A queued prompt is not a
  completed turn, and switching sessions must not discard it.
- Use `ILogger<T>` for diagnostics. `Console.Error.WriteLine` in Blazor WASM
  activates the fatal error banner even for a caught, recoverable failure.
  Log-pipeline recovery must not log back into itself.
- `SessionStatusBar` is inside the composer; model and reasoning share one
  control. Telemetry unavailable from the runtime is `N/A`, never zero.
  Model options come from the runtime and editing is idle-only.
- Keep host/session drawer panes mutually exclusive. Use `HostName` for host
  identity/status and `MagpieMark` for the shared mark rather than local copies.
- Use `100dvh` for mobile viewport height. Do not subtract the AppBar height
  twice; preserve soft-keyboard resizing.
- App-root JavaScript lives in `Magpilot.Web/wwwroot/js`. Shared component
  consumers must ship the referenced modules. Collocated Razor JS can break
  the hub's two-stage static-asset publish.
- Use `OverlayContent` and `ComposerLeadingContent` for host-specific controls.
  Do not add product-aware branches to `ChatView`.
- For clipped MudBlazor content, account for inner slots and `min-width: 0`.
  Scoped `::deep` selectors do not automatically reach every wrapper subtree.
  Keep full values in tooltips when display labels are shortened.
- Docker contexts must exclude `bin`/`obj`, not source `wwwroot` directories.
  Mixed stale scoped-CSS bundles and fresh WASM can silently destroy layout.

## Validation and conventions

Use the smallest relevant existing test project or filter, then broader
solution validation when the change crosses boundaries. The solution uses
xUnit. Preserve AAA structure, descriptive names, `Fact`, and `Theory`.

The shell acceptance test `scripts/test-shim-phase1.sh` drives a running agent.
Its `TEST_HOST_PID` override is useful when an MSYS shell PID is not a Win32
PID. `scripts/test-bootstrap-hook-env.sh` tests hook forwarding without an
agent, container, or network. Terminal probes are interactive diagnostics;
see the [tool guide](tools/tui-matrix/README.md).

Use file-scoped namespaces, primary constructors where appropriate, pattern
matching, obvious-type `var`, collection expressions, and raw multiline
strings. Keep source comments concise, ASCII, and about non-obvious invariants.
An XML comment cannot contain a double hyphen.

Do not commit or push this repository without explicit user permission.
Releases and version bumps are deliberate actions, not a side effect of a fix.

## Documentation ownership

Change behavior and its documentation together:

- API, runtime, ownership, authentication, or UI contracts:
  `docs/architecture.md` and this guide.
- Container configuration, publishing, networking, or storage:
  `deploy/README.md` and directly affected examples.
- Windows install, pairing, or updater behavior: `installer/README.md`.
- Launcher visual behavior and diagnostics: `tools/tui-matrix/README.md`.
- Public capabilities and setup entry points: `README.md`.
- Major design decisions and remaining capability boundaries: `docs/plan.md`.

Keep facts in their owning guide and link from other documents. Do not
reintroduce deployment-specific runbooks, private task-context links, dated
incident narratives, or mirrored personal backlogs here.
