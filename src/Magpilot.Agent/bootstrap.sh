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
         "${HOME_DIR}/.copilot" "${HOME_DIR}/copilot-context" \
         "${HOME_DIR}/.copilot/installed-plugins/chsienki"
chown -R magnus:magnus "${HOME_DIR}"

# --- 2. context-repos / settings.json scaffold ---
# task-context plugin reads ~/.copilot/context-repos.json to know which
# repos contain context.md folders. We seed it with the local
# copilot-context clone if it isn't already configured. Same for
# settings.json -- it needs the plugin marked enabled so the agent's
# AcpClient.BuildPluginDirArgs picks it up via --plugin-dir.
if [[ ! -s "${HOME_DIR}/.copilot/context-repos.json" ]]; then
    cat > "${HOME_DIR}/.copilot/context-repos.json" <<EOF
{
  "repos": [
    {
      "alias": "copilot-context",
      "path": "${HOME_DIR}/copilot-context"
    }
  ]
}
EOF
    chown magnus:magnus "${HOME_DIR}/.copilot/context-repos.json"
    log "wrote default context-repos.json"
fi

if [[ ! -s "${HOME_DIR}/.copilot/settings.json" ]]; then
    cat > "${HOME_DIR}/.copilot/settings.json" <<EOF
{
  "installedPlugins": [
    {
      "name": "task-context",
      "marketplace": "chsienki",
      "enabled": true,
      "cache_path": "${HOME_DIR}/.copilot/installed-plugins/chsienki/task-context"
    }
  ]
}
EOF
    chown magnus:magnus "${HOME_DIR}/.copilot/settings.json"
    log "wrote default settings.json (task-context enabled)"
fi

# --- 3. clone / refresh the task-context plugin ---
# Private repo; uses the gh CLI auth that the port-lowrisk.sh script
# planted at /home/magnus/.config/gh/. If gh isn't authenticated we just
# skip and let the user fix it; bootstrap mustn't block agent startup
# on plugin sync failures.
PLUGIN_DIR="${HOME_DIR}/.copilot/installed-plugins/chsienki/task-context"

# 3a. one-time git wiring as the magnus user: safe.directory exemption
# (host UID/GID can mismatch confusingly across rebuilds) and gh as the
# HTTPS credential helper so private repo clones / pulls just work.
if [[ ! -f "${HOME_DIR}/.gitconfig" ]] || ! grep -q "gh auth git-credential" "${HOME_DIR}/.gitconfig" 2>/dev/null; then
    log "configuring git: safe.directory + gh credential helper"
    su - magnus -c '
        set -e
        git config --global --add safe.directory "*"
        export GH_CONFIG_DIR=/home/magnus/.config/gh
        /home/magnus/bin/gh auth setup-git
    ' || log "git wiring failed (gh not authenticated yet?)"
fi

if [[ ! -d "${PLUGIN_DIR}/.git" ]]; then
    log "cloning chsienki/task-context plugin..."
    su - magnus -c "GH_CONFIG_DIR=${HOME_DIR}/.config/gh ${HOME_DIR}/bin/gh repo clone chsienki/task-context ${PLUGIN_DIR} -- --depth 1" \
        && log "task-context plugin cloned" \
        || log "task-context plugin clone failed (gh auth missing? skill will not load)"
else
    log "task-context plugin present, pulling latest..."
    su - magnus -c "git -C ${PLUGIN_DIR} pull --quiet" \
        || log "task-context plugin pull failed (continuing)"
fi

# --- 4. refresh copilot-context so contexts are current ---
# The plugin also runs `git pull` on each repo when the skill is invoked,
# but we do an early pull at boot so fresh sessions see today's state.
if [[ -d "${HOME_DIR}/copilot-context/.git" ]]; then
    su - magnus -c "git -C ${HOME_DIR}/copilot-context pull --quiet" \
        && log "copilot-context up to date" \
        || log "copilot-context pull failed (continuing)"
fi

# --- 5. retire the legacy phase-B stub at ~/.copilot/skills/task-context ---
# That one was a hand-written linux-only SKILL.md placed before the real
# plugin was wired up. Remove it so we don't have two entries with the
# same name competing for the model's attention.
LEGACY_STUB="${HOME_DIR}/.copilot/skills/task-context"
if [[ -d "${LEGACY_STUB}" && -d "${PLUGIN_DIR}" ]]; then
    rm -rf "${LEGACY_STUB}"
    log "removed legacy task-context stub (replaced by plugin clone)"
fi

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
