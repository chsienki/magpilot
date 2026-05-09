#!/usr/bin/env bash
# Magnus container entrypoint.
#
# Bootstraps two long-running pinned sessions:
#   - Magnus   : the user-facing always-on conversation. Persisted at
#                /home/magnus/.magnus/.session-id, exposed to the SPA, WA
#                sidecar, etc.
#   - Operations: the cron heartbeat / scheduled-job thread. Persisted at
#                /home/magnus/.magnus/.operations-session-id, used by
#                /srv/magpilot-cron/ so heartbeat noise doesn't pollute
#                Magnus's main conversation.
#
# Both ids survive container restarts. After the first boot, this script
# leaves both sessions Dormant so the first client to touch each one
# triggers ACP load (avoids the SPA's empty-on-fresh-tab edge -- see
# /sessions/{id}/history endpoint and docs/architecture.md).

set -euo pipefail

HOME_DIR=/home/magnus
STATE_DIR="${HOME_DIR}/.magnus"
SESSION_ID_FILE="${STATE_DIR}/.session-id"
OPS_SESSION_ID_FILE="${STATE_DIR}/.operations-session-id"
# Magnus's working directory: holds MEMORY.md, IDENTITY.md, todo.py, proxmox keys,
# and is the cwd of both pinned sessions. Renamed from the original "clawd"
# (legacy from OpenClaw) to match the persona.
MAGNUS_DIR="${HOME_DIR}/magnus"
TOKEN="${MAGPILOT_AGENT_TOKEN:-dev-token}"
AGENT_URL="http://127.0.0.1:5099"

log() { echo "[bootstrap] $*"; }

# --- 1. fix up ownership / ensure dirs exist (runs as root on entry) ---
mkdir -p "${HOME_DIR}" "${STATE_DIR}" "${MAGNUS_DIR}" \
         "${HOME_DIR}/.copilot" "${HOME_DIR}/copilot-context"
chown -R magnus:magnus "${HOME_DIR}"

(
    sleep 3
    for i in $(seq 1 60); do
        if curl -fsS -H "Authorization: Bearer ${TOKEN}" \
                "${AGENT_URL}/api/info" >/dev/null 2>&1; then
            break
        fi
        sleep 1
    done

    # Create a named pinned session, send a warm-up prompt so events.jsonl
    # exists (without it, ACP cannot session/load on a future restart),
    # write the new id to the given path. Returns the new sid on stdout.
    create_session() {
        local label="$1"            # e.g. "Magnus" or "Operations"
        local cwd="$2"              # e.g. /home/magnus/magnus
        local id_file="$3"          # where to persist the sid
        local warmup_prompt="$4"    # initialisation prompt
        log "Creating ${label} session..."
        local body
        body=$(printf '{"cwd":"%s","name":"%s","useAgency":false}' "${cwd}" "${label}")
        local create_resp
        create_resp=$(curl -fsS -X POST \
            -H "Authorization: Bearer ${TOKEN}" \
            -H "Content-Type: application/json" \
            -d "${body}" \
            "${AGENT_URL}/api/sessions" || true)
        local new_sid
        new_sid=$(echo "${create_resp}" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]\+\)".*/\1/p' | head -n1)
        if [[ -z "${new_sid}" ]]; then
            log "Failed to create ${label} session: ${create_resp}"
            return 1
        fi
        echo "${new_sid}" > "${id_file}"
        chown magnus:magnus "${id_file}"
        log "Created ${label} session ${new_sid}"

        local warmup_body
        warmup_body=$(printf '{"prompt":"%s","sessionId":"%s","timeoutSeconds":60}' "${warmup_prompt}" "${new_sid}")
        curl -fsS -X POST \
            -H "Authorization: Bearer ${TOKEN}" \
            -H "Content-Type: application/json" \
            -d "${warmup_body}" \
            "${AGENT_URL}/api/quick-prompt" >/dev/null 2>&1 || \
            log "${label} warm-up prompt failed (events.jsonl may not be persisted)"
        echo "${new_sid}"
        return 0
    }

    # Clear any stale lock on an existing session so it shows up Dormant
    # in the registry and the first client (SPA / WA / cron) can adopt it.
    poke_dormant() {
        local sid="$1"
        local label="$2"
        local sd="${HOME_DIR}/.copilot/session-state/${sid}"
        if [[ -d "${sd}" ]]; then
            rm -f "${sd}"/inuse.*.lock
            log "${label} session ${sid} on file (left Dormant)"
        else
            log "${label} session ${sid} on file but events dir missing"
        fi
    }

    # --- Magnus (user-facing) ---
    if [[ ! -s "${SESSION_ID_FILE}" ]]; then
        create_session "Magnus" "${MAGNUS_DIR}" "${SESSION_ID_FILE}" \
            "Initialising memory. Reply with one word: ready." >/dev/null || true
    else
        poke_dormant "$(cat ${SESSION_ID_FILE})" "Magnus"
    fi

    # --- Operations (cron / scheduled jobs) ---
    if [[ ! -s "${OPS_SESSION_ID_FILE}" ]]; then
        create_session "Operations" "${MAGNUS_DIR}" "${OPS_SESSION_ID_FILE}" \
            "I am the Operations channel. Reply: ready." >/dev/null || true
    else
        poke_dormant "$(cat ${OPS_SESSION_ID_FILE})" "Operations"
    fi
) &

log "Starting Magpilot.Agent as magnus..."
cd "${HOME_DIR}"
exec setpriv --reuid=magnus --regid=magnus --init-groups \
    env HOME="${HOME_DIR}" \
        MAGPILOT_AGENT_TOKEN="${TOKEN}" \
        MAGPILOT_AGENT_PUBLIC_URL="${MAGPILOT_AGENT_PUBLIC_URL:-}" \
        ASPNETCORE_URLS="${ASPNETCORE_URLS}" \
        PATH="${PATH}" \
    dotnet /app/Magpilot.Agent.dll
