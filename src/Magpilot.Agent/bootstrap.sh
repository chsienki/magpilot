#!/usr/bin/env bash
# Generic Magpilot agent container entrypoint.
#
# Responsibilities (all generic; nothing site-specific):
#
#   1. Ensure the agent's home directory + ownership are correct.
#   2. Launch the .NET agent process.
#   3. (Optional) Wait for /api/info to come up, then run any *.sh scripts
#      in MAGPILOT_BOOTSTRAP_HOOK_DIR. Hooks inherit AGENT_URL +
#      MAGPILOT_AGENT_TOKEN so they can drive the agent's HTTP API
#      (e.g. to create pinned sessions, install plugins, seed memory
#      files for a particular deployment).
#
# Why hooks?
#   The public Magpilot image is a generic multiplexer. Site-specific
#   behaviour (Magnus's pinned sessions, copilot-context syncing, plugin
#   installation, etc.) lives in the deployer's own repo and is bind-mounted
#   in. Magpilot itself never grows a "Magnus" code path.
#
# Hook contract:
#   - Each hook is an executable .sh file.
#   - Lexical order; convention is NN-name.sh (e.g. 01-pinned-sessions.sh).
#   - Hooks run AFTER the agent's HTTP API answers /api/info.
#   - Hooks should be idempotent (run safely on every container restart).
#   - Hooks SHOULD NOT block. Failures are logged; agent stays up.
#   - Hooks run as the agent user so any disk state they create is owned
#     correctly for subsequent runs.

set -euo pipefail

HOME_DIR="${MAGPILOT_AGENT_HOME:-/home/magnus}"
USER_NAME="${MAGPILOT_AGENT_USER:-magnus}"
TOKEN="${MAGPILOT_AGENT_TOKEN:-dev-token}"
AGENT_URL="${MAGPILOT_AGENT_INTERNAL_URL:-http://127.0.0.1:5099}"
HOOK_DIR="${MAGPILOT_BOOTSTRAP_HOOK_DIR:-}"

log() { echo "[bootstrap] $*"; }

# --- 1. Ensure home dir + ownership (runs as root on entry) ---
mkdir -p "${HOME_DIR}" "${HOME_DIR}/.copilot"
chown -R "${USER_NAME}:${USER_NAME}" "${HOME_DIR}"

# --- 2. (Background) Wait for /api/info, then run hooks ---
if [[ -n "${HOOK_DIR}" && -d "${HOOK_DIR}" ]]; then
    (
        sleep 3
        for i in $(seq 1 60); do
            if curl -fsS -H "Authorization: Bearer ${TOKEN}" \
                    "${AGENT_URL}/api/info" >/dev/null 2>&1; then
                break
            fi
            sleep 1
        done

        log "running hooks from ${HOOK_DIR}"
        shopt -s nullglob
        for hook in "${HOOK_DIR}"/*.sh; do
            local_name=$(basename "${hook}")
            log "  -> ${local_name}"
            su -s /bin/bash - "${USER_NAME}" -c "
                export AGENT_URL='${AGENT_URL}' \
                       MAGPILOT_AGENT_TOKEN='${TOKEN}' \
                       MAGPILOT_AGENT_HOME='${HOME_DIR}'
                bash '${hook}'
            " || log "  ${local_name} exited non-zero (continuing)"
        done
        log "hooks complete"
    ) &
elif [[ -n "${HOOK_DIR}" ]]; then
    log "MAGPILOT_BOOTSTRAP_HOOK_DIR='${HOOK_DIR}' is set but the directory does not exist; skipping hooks"
fi

# --- 3. Launch the agent (foreground, as the agent user) ---
log "starting Magpilot.Agent as ${USER_NAME}..."
cd "${HOME_DIR}"
exec setpriv --reuid="${USER_NAME}" --regid="${USER_NAME}" --init-groups \
    env HOME="${HOME_DIR}" \
        MAGPILOT_AGENT_TOKEN="${TOKEN}" \
        MAGPILOT_AGENT_PUBLIC_URL="${MAGPILOT_AGENT_PUBLIC_URL:-}" \
        MAGPILOT_AGENT_NAME="${MAGPILOT_AGENT_NAME:-}" \
        MAGPILOT_HUB_URL="${MAGPILOT_HUB_URL:-}" \
        MAGPILOT_HUB_BEARER="${MAGPILOT_HUB_BEARER:-}" \
        ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:5099}" \
        PATH="${PATH}" \
    dotnet /app/Magpilot.Agent.dll
