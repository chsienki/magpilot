#!/usr/bin/env bash
# Magnus container entrypoint.
#
# 1. Make sure /home/magnus/{clawd,.copilot,copilot-context} exist with the
#    right ownership (volume mounts can show up root-owned on first boot).
# 2. Drop to the magnus user and exec the .NET agent.
# 3. Once the agent's HTTP API is up, create-or-adopt Magnus's always-on
#    session via the agent's own /api/sessions endpoint. Session id is
#    persisted at /home/magnus/.magnus/.session-id so it survives container
#    restarts.

set -euo pipefail

HOME_DIR=/home/magnus
STATE_DIR="${HOME_DIR}/.magnus"
SESSION_ID_FILE="${STATE_DIR}/.session-id"
CLAWD_DIR="${HOME_DIR}/clawd"
TOKEN="${MAGPILOT_AGENT_TOKEN:-dev-token}"
AGENT_URL="http://127.0.0.1:5099"

log() { echo "[bootstrap] $*"; }

# --- 1. fix up ownership / ensure dirs exist (runs as root on entry) ---
mkdir -p "${HOME_DIR}" "${STATE_DIR}" "${CLAWD_DIR}" \
         "${HOME_DIR}/.copilot" "${HOME_DIR}/copilot-context"
chown -R magnus:magnus "${HOME_DIR}"

# --- 2. background a session-bootstrapper that polls the agent and
#         creates-or-adopts the always-on session once the API answers ---
(
    sleep 3
    for i in $(seq 1 60); do
        if curl -fsS -H "Authorization: Bearer ${TOKEN}" \
                "${AGENT_URL}/api/info" >/dev/null 2>&1; then
            break
        fi
        sleep 1
    done

    create_session() {
        log "Creating Magnus's always-on session..."
        local create_resp
        create_resp=$(curl -fsS -X POST \
            -H "Authorization: Bearer ${TOKEN}" \
            -H "Content-Type: application/json" \
            -d "{\"cwd\":\"${CLAWD_DIR}\",\"useAgency\":false}" \
            "${AGENT_URL}/api/sessions" || true)
        local new_sid
        new_sid=$(echo "${create_resp}" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]\+\)".*/\1/p' | head -n1)
        if [[ -z "${new_sid}" ]]; then
            log "Failed to create session: ${create_resp}"
            return 1
        fi
        echo "${new_sid}" > "${SESSION_ID_FILE}"
        chown magnus:magnus "${SESSION_ID_FILE}"
        log "Created session ${new_sid}"

        # Warm-up: send a trivial prompt so events.jsonl gets written. Without
        # this, the session is empty on disk, and ACP cannot session/load it
        # after a restart (it returns "session not found"). With even one
        # event persisted, future adopts work.
        log "Sending warm-up prompt to persist events.jsonl..."
        curl -fsS -X POST \
            -H "Authorization: Bearer ${TOKEN}" \
            -H "Content-Type: application/json" \
            -d "{\"prompt\":\"Initialising memory. Reply with one word: ready.\",\"sessionId\":\"${new_sid}\",\"timeoutSeconds\":60}" \
            "${AGENT_URL}/api/quick-prompt" >/dev/null 2>&1 || \
            log "Warm-up prompt failed (events.jsonl may not be persisted)"
        echo "${new_sid}"
        return 0
    }

    if [[ ! -s "${SESSION_ID_FILE}" ]]; then
        create_session >/dev/null || true
    else
        sid=$(cat "${SESSION_ID_FILE}")
        log "Session id ${sid} on file. Leaving Dormant -- first client (SPA or WA) will adopt + load history."
        # Clean up stale lock from previous container instance so the session
        # shows up as Dormant in the registry.
        SESSION_DIR="${HOME_DIR}/.copilot/session-state/${sid}"
        if [[ -d "${SESSION_DIR}" ]]; then
            rm -f "${SESSION_DIR}"/inuse.*.lock
        fi
        # NOTE: We deliberately do NOT call /sessions/{id}/adopt here. If we
        # adopt at boot, ACP loads the session and we mark it Owned in the
        # registry. The SPA, when first opened, sees Owned + no client-side
        # cache and connects WITHOUT load=true (because ACP rejects double-
        # load on Owned sessions). Result: SPA shows empty session even
        # though events.jsonl on disk has the full history.
        #
        # By leaving the session Dormant, the FIRST client to touch it (SPA
        # opening with load=true, OR WA's quick-prompt with sessionId via
        # AdoptAsync) will trigger the ACP load and history streams to that
        # client. If you want both SPA history AND WA, open the SPA first
        # after a restart -- subsequent WA messages will appear in the SPA's
        # already-open SSE stream.
    fi
) &

# --- 3. exec the agent as the magnus user, in /home/magnus so any stray
#         working-directory references resolve under the persistent volume.
log "Starting Magpilot.Agent as magnus..."
cd "${HOME_DIR}"
exec setpriv --reuid=magnus --regid=magnus --init-groups \
    env HOME="${HOME_DIR}" \
        MAGPILOT_AGENT_TOKEN="${TOKEN}" \
        MAGPILOT_AGENT_PUBLIC_URL="${MAGPILOT_AGENT_PUBLIC_URL:-}" \
        ASPNETCORE_URLS="${ASPNETCORE_URLS}" \
        PATH="${PATH}" \
    dotnet /app/Magpilot.Agent.dll
