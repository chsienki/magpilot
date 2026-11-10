# Deployment

Run one hub and an agent on each host whose Copilot sessions you want to use.
The published containers target **Linux amd64**. Windows agents and the
terminal launcher use the [Windows installer](../installer/README.md).

Examples use reserved domain names. Substitute your own DNS names, addresses,
and paths; no Proxmox, particular VPN, or home-network layout is required.
See [architecture](../docs/architecture.md) for the API and trust model.

## Network and credentials

| Connection | Default | Protection |
|---|---|---|
| Browser to hub | Hub HTTP on TCP 7088, behind your HTTPS proxy | GitHub OAuth and cookie |
| Hub to agent | Agent HTTP on TCP 5099 | Per-agent bearer |
| Agent to hub | Configured hub URL | Infrastructure bearer, for logs and update polling |
| Agent discovery | Agent listens on UDP 47823 | Address discovery only |
| Interactive pairing discovery | Hub listens on UDP 47824 | Claim approval establishes trust |

Keep agent ports on a trusted LAN or VPN. Built-in hub-to-agent TLS is not
implemented. An agent can run tools with its host user's permissions; neither
bearer authentication nor a container makes arbitrary mounted files safe.

These are separate credentials:

- `MAGPILOT_AGENT_TOKEN` authorizes calls **into one agent**. Enrollment mints
  it and the hub stores the matching token in `hub.db`. The hub does not use a
  shared agent-token environment variable.
- `MAGPILOT_HUB_BEARER` authorizes non-cookie calls **into the hub** and can
  access every agent. Keep it as carefully as an administrator credential.
- Copilot authentication authorizes model/runtime use. A hub OAuth login or
  agent bearer does not supply Copilot access.

Enrollment bundles contain the hub bearer. Do not put them, `.env` files,
Copilot homes, or database backups in source control.

## Set up a hub

Use the repository's [docker-compose.yml](docker-compose.yml) on a Linux Docker
host. It uses host networking for LAN broadcast discovery, binds the hub to
7088, and persists the data directory. It also defines an optional Watchtower
service.

Create a GitHub OAuth App with a callback URL of
`https://magpilot.example.net/oauth/callback`. Copy the Compose file into a
dedicated deployment directory and create a sibling `.env`:

```dotenv
MAGPILOT_HUB_BEARER=replace-with-a-long-random-secret
MAGPILOT_HUB_PUBLIC_URL=https://magpilot.example.net
MAGPILOT_HUB_TRUSTED_PROXIES=127.0.0.1,::1
OAUTH_CLIENT_ID=replace-with-oauth-client-id
OAUTH_CLIENT_SECRET=replace-with-oauth-client-secret
OAUTH_ALLOWED_GITHUB_USERS=your-github-login
```

Generate a secret rather than using the example value, for example with
`openssl rand -hex 32`. The allowlist is comma-separated; its **first entry is
the administrator**. Use only mutually trusted accounts: pending interactive
pairing claims are visible to all authenticated users, not isolated per user.

Set trusted proxy addresses to the actual immediate proxy peers. The loopback
example is appropriate only when the proxy reaches the hub from loopback.
Leave `MAGPILOT_HUB_COOKIE_DOMAIN` unset unless you deliberately want shared
sign-in across trusted sibling subdomains.

Run these commands **on the Linux Docker host**, in that deployment directory:

```bash
mkdir -p data
chmod 600 .env
docker compose config --quiet
docker compose pull hub
docker compose up -d hub
curl -fsS http://127.0.0.1:7088/healthz
```

Then configure HTTPS and sign in through the public URL. Never enable
`MAGPILOT_DEV_BYPASS_AUTH` on a shared deployment.

### Reverse proxy and streaming

Forward to the hub's port 7088 and preserve the original host and HTTPS scheme.
For Nginx or Nginx Proxy Manager, disable buffering/caching and allow long
streaming requests:

```nginx
proxy_buffering off;
proxy_cache off;
gzip off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

Configure `MAGPILOT_HUB_TRUSTED_PROXIES` for the real proxy source addresses.
Incorrect forwarded headers can produce an HTTP OAuth callback instead of
HTTPS. Batched rather than incremental responses usually indicate buffering
in a proxy between browser and agent.

### Hub configuration reference

These are native process settings. If a setting is not already mapped in the
example Compose file, add the corresponding `environment` entry there;
Compose's `.env` file alone does not export every key into the container.

| Variable | Purpose |
|---|---|
| `ASPNETCORE_URLS` | Listen addresses; the image/example uses port 7088 |
| `MAGPILOT_HUB_DATA` | Directory for `hub.db` and `logs.db`; image uses `/data` |
| `MAGPILOT_HUB_PUBLIC_URL` | Reachable hub URL for pairing |
| `MAGPILOT_HUB_BEARER` | Infrastructure API bearer |
| `OAUTH_CLIENT_ID`, `OAUTH_CLIENT_SECRET` | GitHub OAuth App credentials |
| `OAUTH_ALLOWED_GITHUB_USERS` | Allowed logins, administrator first |
| `MAGPILOT_HUB_TRUSTED_PROXIES` | Comma-separated immediate proxy IP addresses |
| `MAGPILOT_HUB_COOKIE_DOMAIN` | Optional cookie sharing domain |
| `MAGPILOT_RELEASE_REPO` | Release repository, default `chsienki/magpilot` |
| `MAGPILOT_GITHUB_TOKEN` | Optional GitHub API token; required for a private release repository |
| `MAGPILOT_DEV_BYPASS_AUTH` | Development only; must not be enabled in production |

ASP.NET configuration also exposes `Hub:DiscoveryIntervalSec` (60),
`Hub:AgentStaleSec` (180), `Hub:AgentHttpTimeoutSec` (10), and
`Hub:AgentActionTimeoutSec` (90). In environment variables use double
underscores, for example `Hub__AgentActionTimeoutSec`.

## Add Windows hosts

Install the agent and launcher, then pair through **Agents** in the hub.
Use [interactive pairing or an enrollment bundle](../installer/README.md#pairing).
Discovery alone does not give the hub credentials.

Interactive pairing writes the hub URL and agent token, but **does not supply
the hub bearer**. For log forwarding and update polling, either use an
enrollment bundle or provision `MAGPILOT_HUB_BEARER` separately in
`magpilot.env`, then restart the agent.

## Generic Linux agent image

The public image is `ghcr.io/chsienki/magpilot-agent`. It includes the agent,
bundled Copilot runtime, and helper tools for deployment hooks. It does not
contain your credentials, repositories, custom agents, or pinned sessions.

### Enroll the container identity

Enrollment must use the container's actual hostname, because discovery reports
`Environment.MachineName`. `MAGPILOT_AGENT_NAME` controls log labeling; it does
not override discovery identity.

Create a bundle on `/admin/enroll`. On a provisioning machine with the Windows
launcher, use a fresh PowerShell terminal to write settings for a container
named `linux-agent` without touching the installed agent:

```pwsh
$deploymentDir = Join-Path $env:USERPROFILE "magpilot-linux-agent"
New-Item -ItemType Directory -Force $deploymentDir | Out-Null
$env:MAGPILOT_AGENT_NAME = "linux-agent"
$env:MAGPILOT_ENV_FILE = Join-Path $deploymentDir ".env"
magpilot "--magpilot-pair=<bundle>"
Remove-Item Env:\MAGPILOT_AGENT_NAME, Env:\MAGPILOT_ENV_FILE
```

Replace `<bundle>` with the generated value. From a Windows source checkout
with the .NET 10 SDK, the equivalent is
`dotnet run --project .\src\Magpilot.Host -- "--magpilot-pair=<bundle>"`.
There is no packaged Linux launcher installer in this repository.

Securely transfer the generated `.env` to the Linux deployment directory.
Keep its hub URL, agent token, and hub bearer; add Copilot authentication and
the agent address the hub can reach:

```dotenv
COPILOT_GITHUB_TOKEN=replace-with-a-token-authorized-for-copilot
MAGPILOT_AGENT_PUBLIC_URL=http://agent.example.net:5099
```

### Run the agent

Create a separate Compose file beside that `.env`:

```yaml
services:
  agent:
    image: ghcr.io/chsienki/magpilot-agent:latest
    hostname: linux-agent
    restart: unless-stopped
    network_mode: host
    environment:
      ASPNETCORE_URLS: http://0.0.0.0:5099
      MAGPILOT_AGENT_TOKEN: ${MAGPILOT_AGENT_TOKEN:?enroll this agent first}
      MAGPILOT_AGENT_NAME: linux-agent
      MAGPILOT_AGENT_PUBLIC_URL: ${MAGPILOT_AGENT_PUBLIC_URL:?set a reachable URL}
      MAGPILOT_HUB_URL: ${MAGPILOT_HUB_URL:?set the hub URL}
      MAGPILOT_HUB_BEARER: ${MAGPILOT_HUB_BEARER:?set the hub bearer}
      COPILOT_GITHUB_TOKEN: ${COPILOT_GITHUB_TOKEN:?configure Copilot authentication}
    volumes:
      - ./home:/home/magnus
```

The image's default account and home are `magnus` and `/home/magnus`. This is
an image default, not a requirement to use a particular assistant product.
Use a dedicated bind mount: bootstrap fixes ownership recursively before
starting the agent as that non-root user. A different account requires an
image containing that account plus matching `MAGPILOT_AGENT_USER` and
`MAGPILOT_AGENT_HOME`.

Mount the repositories and configuration the agent actually needs. Keep its
home persistent so Copilot sessions survive recreation. Do not mount your
entire host home merely to make authentication work.

On the Docker host:

```bash
chmod 600 .env
docker compose config --quiet
docker compose pull agent
docker compose up -d agent
curl -fsS http://127.0.0.1:5099/healthz
curl -fsS http://127.0.0.1:5099/api/version
```

Verify a real authenticated session through the hub as well; health endpoints
do not prove Copilot authentication, model access, or tool permissions work.

### Bootstrap hooks

Mount your hook directory and set `MAGPILOT_BOOTSTRAP_HOOK_DIR` to its container
path. Bootstrap waits for the authenticated agent API and runs `*.sh` files
in lexical order as the agent user. Hooks should be idempotent and finish
quickly; failures are logged and do not stop the agent. Hook completion is not
part of `/healthz`.

Hooks receive `AGENT_URL`, `MAGPILOT_AGENT_TOKEN`, and `MAGPILOT_AGENT_HOME`.
The internal URL defaults to `http://127.0.0.1:5099`; override
`MAGPILOT_AGENT_INTERNAL_URL` if the listener differs.

For extra settings, `MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST` is a comma- or
whitespace-separated list of **variable names**, never assignments. Entries
must match `[A-Za-z_][A-Za-z0-9_]*`. Unset or invalid entries are skipped;
pathname expansion is disabled while parsing. Login-shell variables and
shell/loader startup controls are refused, including `HOME`, `PATH`,
`BASH_ENV`, `ENV`, `SHELLOPTS`, `BASHOPTS`, `LD_*`, `GLIBC_TUNABLES`,
`GCONV_PATH`, and `LOCPATH`.

Values cross the privilege drop through `su --whitelist-environment`, not
shell interpolation. Preserve that boundary when extending bootstrap.
The implementation is [bootstrap.sh](../src/Magpilot.Agent/bootstrap.sh);
`scripts/test-bootstrap-hook-env.sh` tests forwarding without a running agent.

### Agent settings

| Variable | Purpose |
|---|---|
| `MAGPILOT_AGENT_TOKEN` | Agent's inbound bearer |
| `MAGPILOT_AGENT_PUBLIC_URL` | Address advertised to discovery; not the bind address |
| `MAGPILOT_AGENT_NAME` | Central-log source label |
| `MAGPILOT_HUB_URL`, `MAGPILOT_HUB_BEARER` | Both needed for forwarded logs and update checks |
| `MAGPILOT_ENV_FILE` | Native agent settings load path and launcher pairing output path |
| `MAGPILOT_AUTO_APPROVE` | `true` auto-approves ordinary SDK permission requests |
| `MAGPILOT_YOLO_DISABLED` | `true` blocks per-session YOLO changes, not agent-wide auto-approval |
| `MAGPILOT_TURN_STALL_SECONDS` | Silent-turn threshold, default 90; open tool calls are exempt |
| `MAGPILOT_BOOTSTRAP_HOOK_DIR` | Optional container hook directory |
| `MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST` | Extra environment names forwarded to hooks |

Avoid automatic approval on a workstation holding the user's unrestricted
files and credentials. Managed-required permissions always need a human.
Per-session YOLO is in memory and does not survive agent restart.

## Discovery across networks

Host networking lets the example containers participate in the host's LAN
broadcast domain. Broadcasts do not traverse routed VPNs, and some Wi-Fi access
points filter them. An agent URL override only affects replies that reach the
hub; it does not register that address by itself.

There is no `POST /api/agents` registration endpoint. For an enrolled but
undiscoverable agent, stop the hub, back up `hub.db`, and seed its existing
registry row with a reachable address:

```sql
UPDATE agents
SET url = 'http://agent.example.net:5099'
WHERE name = 'linux-agent';
```

Restart the hub to reload the registry, then open the agent in the browser.
Successful proxied calls refresh online state; a host without discovery can
look stale until contacted. Do not edit bearer tokens or owners to work
around a routing problem.

## Updates and rollback

Both public images are published by GitHub Actions after build/test checks:

| Trigger | Hub tags | Agent tags |
|---|---|---|
| Push to `main` | `main`, `main-<short-sha>` | `main`, `main-<short-sha>` |
| Tag `vX.Y.Z` | `X.Y.Z`, `X.Y`, `latest` | `X.Y.Z`, `X.Y`, `latest` |

Main builds do not move production `latest`. The agent workflow also offers
a main-only manual `seed_latest_from_main` input for the first publish;
ordinary dispatches leave `latest` alone. Fork maintainers must make GHCR
packages public if deployments should pull anonymously.

For a manual hub update:

```bash
docker compose pull hub
docker compose up -d hub
docker compose logs --tail 50 hub
curl -fsS http://127.0.0.1:7088/healthz
```

The example's label-gated Watchtower is optional:

```bash
docker compose up -d watchtower
```

It polls every five minutes and recreates labeled containers when the selected
tag's digest changes. It never switches a pinned version tag to the next
version. To prevent all automatic changes, do not run Watchtower or remove the
service's opt-in label. Agents can opt into an existing watcher independently;
the generic agent image does not install a watcher.

Pin a tested version tag or digest for controlled rollout and rollback.
Recreation interrupts active requests and reruns agent hooks. Back up
persistent data before an upgrade; rolling an image back does not reverse data
or protocol changes. **Protocol 0 requires matching component releases.**

### Windows update discovery

The hub polls published GitHub Releases at startup and hourly. Configured
agents poll the hub after a startup delay and every 15 minutes; launchers query
the agent's cached metadata on invocation. **Agents -> Check for agent
updates** bypasses the poll delays, not the install step.

Run `magpilot --magpilot-update` on Windows to install. If an interactively
paired agent reports update polling is unconfigured, supply its missing hub
bearer or pair with an enrollment bundle. Draft releases, unpublished assets,
or a private release repository without `MAGPILOT_GITHUB_TOKEN` also prevent
update discovery.

### Release maintainers

`VERSION` is the release source of truth for normal source builds through
`Directory.Build.props`. After committing the intended version:

1. Push the matching `vX.Y.Z` tag. The Windows release workflow validates it
   against `VERSION`, builds/tests, publishes agent and Native AOT launcher,
   and creates an unsigned installer with SHA256 and `version.json` assets.
2. Review and publish the draft GitHub Release so installers and hub release
   discovery can find it.
3. Verify the running hub's `/api/agent-version` reports that version, refresh
   agents, and exercise an actual Windows update.

Container image workflows start on the **tag push**, independently of
publishing the draft Windows release. A Watchtower deployment can therefore
update before the installer becomes discoverable. Pin images when the rollout
must remain coordinated.

### Local image builds

Build from the repository root in PowerShell:

```pwsh
docker buildx build --platform linux/amd64 `
    -f .\src\Magpilot.Hub\Dockerfile -t magpilot-hub:local --load .
docker buildx build --platform linux/amd64 `
    -f .\src\Magpilot.Agent\Dockerfile -t magpilot-agent:local --load .
```

Reference the chosen tag in your deployment. For another Docker host, use
`docker save`, transfer the archive, and `docker load`. Use a unique local tag
and disable automatic replacement for a durable emergency override; loading
an image under a watched `latest` tag can be undone by the watcher.

## Backups and troubleshooting

Back up the hub's data directory and `.env`, each agent's persistent Copilot
home and needed workspaces, and any deployment-owned hooks. These backups
contain credentials and conversation history. Stop writers or use a
SQLite-aware backup procedure rather than copying an actively changing
database without its WAL state.

Check the browser's **Logs** page (`/admin/logs`, administrator only) for
agent warnings and UI failures. Use `docker compose logs --tail 100 hub` or the
corresponding agent service for startup failures. A healthy `/healthz` does
not establish that an agent is paired, reachable by the hub, or able to run
Copilot.
