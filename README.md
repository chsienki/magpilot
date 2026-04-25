# clawpilot

> Bring the GitHub Copilot CLI experience to your phone.

`clawpilot` is a small system that lets you drive `copilot` (the GitHub
Copilot CLI) from your phone, against any of your computers, over your
home VPN.

It is **not** another agent platform. It does not have plugins, cron
jobs, web dashboards, or a smart-home integration. (For that, see
[openclaw](https://github.com/openclaw/openclaw).) Clawpilot's only
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
streams, and is the single endpoint the phone talks to. This makes
the WireGuard story simple: the phone has one address book entry
forever (`https://copilot.home.sienkiewi.cz` or the LAN IP), and
the hub deals with finding and routing to the actual hosts.

A **.NET MAUI app** on the Pixel renders the chats, modals, and
sessions tree. Notifications go via Firebase Cloud Messaging.

## Components

| Name             | What                                                     | Where it runs              |
|------------------|----------------------------------------------------------|----------------------------|
| `copilot-agent`  | ACP-to-HTTP/SSE adapter, one per machine                 | HENDRIK, Mac, etc.         |
| `copilot-hub`    | Aggregator, discovery, push, single phone-facing endpoint | docker LXC (CT 102)        |
| `CopilotChat`    | .NET MAUI Android app                                    | Pixel (later: iOS, macOS)  |

## Status

Pre-implementation. The full design -- including alternatives
considered, open questions, and a step-by-step build order -- lives
in [`docs/plan.md`](docs/plan.md).

## Repository layout

```
clawpilot/
   README.md                 <- this file
   docs/
      plan.md                <- the design doc (start here)
   agent/                    <- copilot-agent (.NET 9), TBD
   hub/                      <- copilot-hub (.NET 9), TBD
   app/                      <- CopilotChat (.NET MAUI), TBD
```

## Related context

- The home-network and openclaw task-context docs live in a separate
  repo at `chsienki/copilot-context`. Clawpilot intentionally lives
  on its own so the codebase, issues, and history are all in one
  place once implementation starts.
