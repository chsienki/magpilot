# Magpilot deploy

Deployment artifacts for running the hub on the docker LXC (102) in the home lab.

## Layout

```
/srv/magpilot/
  docker-compose.yml
  .env                 # MAGPILOT_AGENT_TOKEN=...
                       # MAGPILOT_HUB_BEARER=...
                       # MAGPILOT_HUB_COOKIE_DOMAIN=.home.sienkiewi.cz
                       # MAGPILOT_HUB_TRUSTED_PROXIES=127.0.0.1,::1
                       # OAUTH_CLIENT_ID=...
                       # OAUTH_CLIENT_SECRET=...
                       # OAUTH_ALLOWED_GITHUB_USERS=chsienki
  data/                # SQLite cache (hub.db, logs.db)
```

## Build + ship

From the repo root on a build host with docker:

```powershell
docker buildx build --platform linux/amd64 `
    -f src/Magpilot.Hub/Dockerfile `
    -t magpilot-hub:latest --load .

docker save magpilot-hub:latest -o magpilot-hub.tar
ssh proxmox "pct push 102 - /tmp/magpilot-hub.tar" < magpilot-hub.tar
ssh proxmox "pct exec 102 -- docker load -i /tmp/magpilot-hub.tar"
ssh proxmox "pct exec 102 -- rm /tmp/magpilot-hub.tar"

ssh proxmox "pct exec 102 -- mkdir -p /srv/magpilot/data"
Get-Content deploy\docker-compose.yml | ssh proxmox `
    "pct exec 102 -- bash -c 'cat > /srv/magpilot/docker-compose.yml'"
ssh proxmox "pct exec 102 -- bash -c 'cd /srv/magpilot && docker compose up -d'"
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

## HENDRIK agent prerequisites

```powershell
$env:MAGPILOT_AGENT_TOKEN      = '<shared-secret>'
$env:MAGPILOT_AGENT_PUBLIC_URL = 'http://192.168.1.248:5099'

New-NetFirewallRule -DisplayName 'Magpilot Agent (TCP 5099)' `
    -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow `
    -RemoteAddress 192.168.1.0/24,192.168.4.0/24
New-NetFirewallRule -DisplayName 'Magpilot Agent Discovery (UDP 47823)' `
    -Direction Inbound -Protocol UDP -LocalPort 47823 -Action Allow `
    -RemoteAddress 192.168.1.0/24,192.168.4.0/24
```
