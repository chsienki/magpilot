# Magnus -- the always-on Magpilot instance on LXC 102

This directory holds the deployment artifacts for **Magnus**, an always-on
Magpilot agent that runs in a Docker container on LXC 102 alongside the
magpilot-hub stack. Magnus's job is to host a long-running session that
the user can talk to from anywhere via the Magpilot SPA, with persistent
memory in `~/clawd/` and skills/MCPs configured under `~/.copilot/`.

It exists to absorb the role that OpenClaw plays today (an always-on,
multi-channel personal assistant) while keeping Magpilot's clean
separation between hub, agents, and external sidecars (WhatsApp, cron,
etc.). See `clawpilot` repo's plan.md / checkpoints for the full design.

## Files

- `docker-compose.yml` -- the stack definition. Lives at
  `/srv/magpilot-agent/docker-compose.yml` on the LXC.
- `.env` (not in git) -- holds `MAGPILOT_AGENT_TOKEN` (matches hub) and
  `COPILOT_GITHUB_TOKEN` (fine-grained PAT with "Copilot Requests").

## Build + deploy

From the repo root on a workstation:

```bash
docker build -f src/Magpilot.Agent/Dockerfile -t magpilot-agent:latest .
docker save magpilot-agent:latest | gzip > magpilot-agent.tar.gz
scp magpilot-agent.tar.gz proxmox:/tmp/
ssh proxmox 'pct push 102 /tmp/magpilot-agent.tar.gz /tmp/magpilot-agent.tar.gz'
ssh proxmox 'pct exec 102 -- bash -c "docker load < /tmp/magpilot-agent.tar.gz"'

# First-time only: provision the stack dir + .env on the LXC.
ssh proxmox 'pct exec 102 -- mkdir -p /srv/magpilot-agent/home'
scp deploy/magnus/docker-compose.yml proxmox:/tmp/
ssh proxmox 'pct push 102 /tmp/docker-compose.yml /srv/magpilot-agent/docker-compose.yml'

# Bring up:
ssh proxmox 'pct exec 102 -- bash -c "cd /srv/magpilot-agent && docker compose up -d"'
```

## Verify

```bash
ssh proxmox 'pct exec 102 -- docker logs --tail 50 magnus'
ssh proxmox 'pct exec 102 -- curl -fsS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5099/api/info'
```

The Magpilot SPA at https://magpilot.home.sienkiewi.cz should show
`magnus` as a host within ~5s of the agent being up (UDP discovery).
