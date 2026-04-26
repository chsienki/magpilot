# Clawpilot deploy

Deployment artifacts for running the hub on the docker LXC (102) in the home lab.

## Layout (mirror of `/srv/openclaw`)

```
/srv/clawpilot/
  docker-compose.yml
  .env                 # CLAWPILOT_AGENT_TOKEN=...
  data/                # SQLite cache (hub.db)
```

## Build + ship

From the repo root on a build host with docker:

```powershell
docker buildx build --platform linux/amd64 `
    -f src/Clawpilot.Hub/Dockerfile `
    -t clawpilot-hub:latest --load .

docker save clawpilot-hub:latest -o clawpilot-hub.tar
ssh proxmox "pct push 102 - /tmp/clawpilot-hub.tar" < clawpilot-hub.tar
ssh proxmox "pct exec 102 -- docker load -i /tmp/clawpilot-hub.tar"
ssh proxmox "pct exec 102 -- rm /tmp/clawpilot-hub.tar"

ssh proxmox "pct exec 102 -- mkdir -p /srv/clawpilot/data"
Get-Content deploy\docker-compose.yml | ssh proxmox `
    "pct exec 102 -- bash -c 'cat > /srv/clawpilot/docker-compose.yml'"
ssh proxmox "pct exec 102 -- bash -c 'cd /srv/clawpilot && docker compose up -d'"
```

## NPM proxy host

`clawpilot.home.sienkiewi.cz` -> `http://192.168.1.239:7088`,
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
$env:CLAWPILOT_AGENT_TOKEN      = '<shared-secret>'
$env:CLAWPILOT_AGENT_PUBLIC_URL = 'http://192.168.1.248:5099'

New-NetFirewallRule -DisplayName 'Clawpilot Agent (TCP 5099)' `
    -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow `
    -RemoteAddress 192.168.1.0/24,192.168.4.0/24
New-NetFirewallRule -DisplayName 'Clawpilot Agent Discovery (UDP 47823)' `
    -Direction Inbound -Protocol UDP -LocalPort 47823 -Action Allow `
    -RemoteAddress 192.168.1.0/24,192.168.4.0/24
```
