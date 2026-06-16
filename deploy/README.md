# Magpilot deploy

Deployment artifacts for running the hub on the docker LXC (102) in the home lab.

## Layout

```
/srv/magpilot/
  docker-compose.yml
  .env                 # MAGPILOT_HUB_BEARER=...                (for non-cookie API callers: MAUI app, agent log POSTs)
                       # MAGPILOT_HUB_COOKIE_DOMAIN=.home.sienkiewi.cz
                       # MAGPILOT_HUB_TRUSTED_PROXIES=127.0.0.1,::1
                       # OAUTH_CLIENT_ID=...
                       # OAUTH_CLIENT_SECRET=...
                       # OAUTH_ALLOWED_GITHUB_USERS=chsienki
                       # MAGPILOT_HUB_PUBLIC_URL=https://magpilot.home.sienkiewi.cz
  data/                # SQLite cache (hub.db, logs.db). Per-agent
                       # bearer tokens live in agents.token here --
                       # the hub no longer reads a shared
                       # MAGPILOT_AGENT_TOKEN env var.
```

## Build + ship

The hub image is built and published by GitHub Actions
(`.github/workflows/hub-image.yml`) on every push to `main` and on
every `vX.Y.Z` tag. Published as a public package to
`ghcr.io/chsienki/magpilot-hub`. Tag conventions:

| Trigger | Tags |
|---|---|
| Push to main | `:main`, `:main-<short-sha>` |
| Tag push v0.1.11 | `:0.1.11`, `:0.1`, `:latest` |

The LXC's `watchtower` service (declared in `docker-compose.yml`)
polls GHCR every 5 min for a new digest of the `:latest` tag. When
it finds one, it pulls + recreates the hub container in place. No
manual `docker save`/`scp`/`pct push` dance for ordinary releases.

### Watching the rollout

After publishing a release tag (`gh release edit vX.Y.Z --draft=false`),
the workflow takes ~5-8 min to push the new image. Then watchtower's
next 5-min poll picks it up and recreates the hub. Total
release-to-deployed time is on the order of 15 min worst-case.

To verify a deployment landed:

```powershell
# Image digest the LXC is currently running (compare against GHCR's :latest).
ssh proxmox "pct exec 102 -- docker inspect magpilot-hub --format '{{ index .Config.Labels \"org.opencontainers.image.version\" }}'"
ssh proxmox "pct exec 102 -- docker logs --tail 50 magpilot-watchtower"
```

### Force an immediate update

If you've just published and don't want to wait for the next poll:

```powershell
ssh proxmox "pct exec 102 -- bash -c 'cd /srv/magpilot && docker compose pull hub && docker compose up -d hub'"
```

### Pinning to a specific version

For staging or rollback, edit `/srv/magpilot/docker-compose.yml` to
reference an immutable version tag (e.g. `:0.1.11` instead of
`:latest`) and `docker compose up -d hub`. Watchtower respects
explicit tags -- it doesn't auto-bump pinned versions, only the
floating `:latest`. Switching back to auto-update is just flipping
the tag back to `:latest`.

### Emergency local build

When CI is down or you're shipping an unreviewed patch and need the
LXC to run a build that doesn't exist on GHCR yet, the old
save/scp/pct-push flow still works:

```powershell
docker buildx build --platform linux/amd64 `
    -f src/Magpilot.Hub/Dockerfile `
    -t ghcr.io/chsienki/magpilot-hub:emergency-$(Get-Date -Format yyyyMMdd-HHmm) --load .

docker save ghcr.io/chsienki/magpilot-hub:emergency-... -o magpilot-hub.tar
ssh proxmox "pct push 102 - /tmp/magpilot-hub.tar" < magpilot-hub.tar
ssh proxmox "pct exec 102 -- docker load -i /tmp/magpilot-hub.tar"
# Edit /srv/magpilot/docker-compose.yml to point at the emergency tag,
# then docker compose up -d hub. Watchtower will skip it (the tag isn't
# :latest) until you flip back.
```

### First-time setup on a fresh LXC

```powershell
ssh proxmox "pct exec 102 -- mkdir -p /srv/magpilot/data"
Get-Content deploy\docker-compose.yml | ssh proxmox `
    "pct exec 102 -- bash -c 'cat > /srv/magpilot/docker-compose.yml'"
# Create /srv/magpilot/.env with the secrets above, then:
ssh proxmox "pct exec 102 -- bash -c 'cd /srv/magpilot && docker compose up -d'"
# Watchtower starts alongside the hub; first hub pull is from GHCR.
```


## NPM proxy host

`magpilot.home.sienkiewi.cz` -> `http://192.168.1.239:7088`,
WebSocket Support enabled, plus this Advanced config for SSE pass-through:

```nginx
proxy_buffering off;
proxy_cache off;
gzip off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

## HENDRIK agent (Windows)

HENDRIK runs the **installed `MagpilotAgent` scheduled task**, shipped
by the magpilot Windows installer (`installer/magpilot.iss`, GitHub
release `vX.Y.Z` -> `magpilot-setup-X.Y.Z.exe`).

```powershell
# First install (or download from a published GitHub release):
& 'D:\path\to\magpilot-setup-X.Y.Z.exe'
# Wizard collects target dir + scheduled-task settings only -- no
# secrets, no hub URL. After the files copy and the scheduled task
# registers, the installer kicks off `magpilot --magpilot-pair`
# (V3 interactive discovery): UDP-broadcast for a hub, browser
# opens to /admin/agents?pending=<id>, you click Adopt, the hub
# mints a per-agent token, the launcher writes magpilot.env, the
# scheduled task is bounced. See magpilot/README.md "Install"
# section for the full walk-through (and the V2a bundle path for
# headless / scripted installs).

# In-place upgrade (preserves existing magpilot.env, no re-pair):
magpilot --magpilot-update

# Manual operations:
Stop-ScheduledTask  -TaskName MagpilotAgent
Start-ScheduledTask -TaskName MagpilotAgent
Get-ScheduledTaskInfo -TaskName MagpilotAgent | Select LastRunTime, LastTaskResult
```

The installer registers two inbound firewall rules (TCP 5099 + UDP
47823) restricted to RFC1918 ranges. For dev iteration: stop the
scheduled task, run `dotnet run --project src/Magpilot.Agent` locally,
then restart the task. See the magpilot copilot-instructions for the
full dev-loop story.
