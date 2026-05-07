# magpilot-cron

A tiny cron-driven dispatcher that posts pre-defined prompt templates to
the Magpilot hub's `quick-prompt` endpoint and (eventually) delivers
responses to channels like WhatsApp.

This replaces the cron job system that lived inside OpenClaw, with a
much simpler architecture: cron + bash + curl, talking to magpilot's
HTTP API just like any other client. No special access to magpilot
internals required.

## Layout

```
/srv/magpilot-cron/
|-- run.sh        # the dispatcher
|-- jobs.yaml     # job definitions
\-- /etc/cron.d/magpilot-cron   # cron schedule
```

## Test

```bash
ssh proxmox 'pct exec 102 -- /srv/magpilot-cron/run.sh magnus-heartbeat'
ssh proxmox 'pct exec 102 -- tail -30 /var/log/magpilot-cron/magnus-heartbeat.log'
```

## Adding a job

1. Add an entry to `jobs.yaml` (mind the 6-space indent on the prompt body).
2. Add a cron line to `/etc/cron.d/magpilot-cron`.
3. Test interactively: `/srv/magpilot-cron/run.sh <job-name>`.

## Delivery channels

- `log` (default) -- write the response to `/var/log/magpilot-cron/<job>.log`.
- `stdout` -- emit to stdout; cron mails this to root if MAILTO is set.
- `whatsapp` -- POST to the WhatsApp sidecar's outbound endpoint
  (Phase E; the sidecar isn't deployed yet).
