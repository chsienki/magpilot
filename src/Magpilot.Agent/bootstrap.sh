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
#   - Hooks always receive AGENT_URL, MAGPILOT_AGENT_TOKEN and
#     MAGPILOT_AGENT_HOME.
#   - MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST names ADDITIONAL environment
#     variables to forward from the container environment into every hook.
#     Comma- and/or whitespace-separated. Magpilot never interprets these
#     names or values -- a deployer uses it to hand its own hooks its own
#     settings (models, feature flags, extra tokens) without magpilot growing
#     a code path for any of them.
#
#     Contract:
#       * Each entry must be a shell variable name ([A-Za-z_][A-Za-z0-9_]*).
#         Anything else is logged and skipped, so a typo -- or an injected
#         "NAME=value; rm -rf /" string -- can never become shell.
#       * Names su(1) always resets for a login shell (HOME, SHELL, USER,
#         LOGNAME, PATH, IFS) are refused: they cannot be forwarded this way.
#       * Shell/dynamic-loader startup controls (BASH_ENV, ENV, SHELLOPTS,
#         BASHOPTS, every LD_* name, GLIBC_TUNABLES, GCONV_PATH, LOCPATH) are
#         refused because forwarding them would allow a value to affect code
#         loading before the hook runs.
#       * Names not set in the container environment are skipped.
#       * Values cross the privilege drop through su's
#         --whitelist-environment, so they are forwarded verbatim and never
#         re-parsed, expanded, eval'd or interpolated into a shell string.
#         Quotes, spaces, $ and ; in a value are safe.

set -euo pipefail

HOME_DIR="${MAGPILOT_AGENT_HOME:-/home/magnus}"
USER_NAME="${MAGPILOT_AGENT_USER:-magnus}"
TOKEN="${MAGPILOT_AGENT_TOKEN:-dev-token}"
AGENT_URL="${MAGPILOT_AGENT_INTERNAL_URL:-http://127.0.0.1:5099}"
HOOK_DIR="${MAGPILOT_BOOTSTRAP_HOOK_DIR:-}"
HOOK_ENV_ALLOWLIST="${MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST:-}"

# Variables su(1) resets for a login shell whatever the whitelist says, so
# claiming to forward them would be a lie.
SU_RESERVED_ENV=" HOME SHELL USER LOGNAME PATH IFS "
# These names alter shell startup or dynamic-loader behaviour. Even though the
# allowlist is deployer-controlled, accepting them would make the claim that
# values are never interpreted as code untrue (for example, bash sources
# BASH_ENV before executing a non-interactive -c command).
HOOK_UNSAFE_ENV=" BASH_ENV ENV SHELLOPTS BASHOPTS GLIBC_TUNABLES GCONV_PATH LOCPATH "

log() { echo "[bootstrap] $*"; }

# A shell variable name, and nothing else. This is the whole safety story for
# the allowlist: only NAMES are ever parsed, values are only ever copied.
is_env_name() {
    [[ "$1" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]
}

# Comma list for `su --whitelist-environment`: the three variables the hook API
# always provides, plus every valid, set, non-reserved name the deployer
# allowlisted. The list goes to stdout; diagnostics go to stderr so they cannot
# contaminate it.
hook_env_list() {
    local requested name list="" restore_globbing=1
    requested="${HOOK_ENV_ALLOWLIST//,/ }"

    # The requested entries still need shell whitespace splitting, but never
    # pathname expansion. In particular, a literal '*' must reach is_env_name
    # as '*' rather than expanding to a file or directory name that happens to
    # also be a valid environment variable.
    [[ "$-" == *f* ]] && restore_globbing=0
    set -f
    for name in AGENT_URL MAGPILOT_AGENT_TOKEN MAGPILOT_AGENT_HOME ${requested}; do
        if ! is_env_name "${name}"; then
            log "  ignoring hook env entry '${name}': not a shell variable name" >&2
            continue
        fi
        if [[ "${SU_RESERVED_ENV}" == *" ${name} "* ]]; then
            log "  ignoring hook env entry '${name}': su always resets it for a login shell" >&2
            continue
        fi
        if [[ "${name}" == LD_* || "${HOOK_UNSAFE_ENV}" == *" ${name} "* ]]; then
            log "  ignoring hook env entry '${name}': controls shell or loader startup" >&2
            continue
        fi
        if [[ -z "${!name+set}" ]]; then
            log "  skipping hook env entry '${name}': not set in this environment" >&2
            continue
        fi
        [[ " ${list} " == *" ${name} "* ]] && continue
        list="${list}${list:+ }${name}"
    done
    ((restore_globbing)) && set +f
    printf '%s' "${list// /,}"
}

# Sourced by scripts/test-bootstrap-hook-env.sh to exercise the helpers above
# without running the container entrypoint below.
if [[ "${MAGPILOT_BOOTSTRAP_LIB_ONLY:-}" == "1" ]]; then
    return 0 2>/dev/null || exit 0
fi

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

        # Export the hook API contract so it crosses the privilege drop the same
        # way everything else does -- through su's whitelist, not through a
        # shell string the token would have to be quoted into.
        export AGENT_URL
        export MAGPILOT_AGENT_TOKEN="${TOKEN}"
        export MAGPILOT_AGENT_HOME="${HOME_DIR}"
        hook_env=$(hook_env_list)
        log "running hooks from ${HOOK_DIR} (env: ${hook_env})"

        shopt -s nullglob
        for hook in "${HOOK_DIR}"/*.sh; do
            local_name=$(basename "${hook}")
            log "  -> ${local_name}"
            # The -c script is a fixed literal and the hook path arrives as a
            # positional argument, so no path, token or allowlisted value is
            # ever interpolated into shell source.
            su -w "${hook_env}" -s /bin/bash - "${USER_NAME}" \
                -c 'exec bash "$1"' magpilot-hook "${hook}" \
                || log "  ${local_name} exited non-zero (continuing)"
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
