#!/usr/bin/env bash
# Bootstrap hook environment allowlist test
# ============================================================================
#
# Unit-tests the MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST contract implemented in
# src/Magpilot.Agent/bootstrap.sh. No container, no agent, no network: the
# entrypoint is sourced in library mode (MAGPILOT_BOOTSTRAP_LIB_ONLY=1) so the
# helpers can be called directly.
#
# What matters here is that only NAMES are ever parsed. Values are handed to
# su(1) via --whitelist-environment and never interpolated into shell source,
# so a value containing quotes, spaces, $ or ; cannot become code.
#
# Usage:
#   ./scripts/test-bootstrap-hook-env.sh
# ============================================================================

set -uo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
BOOTSTRAP="${SCRIPT_DIR}/../src/Magpilot.Agent/bootstrap.sh"

fail_count=0

check() {
    local label="$1" expected="$2" actual="$3"
    if [[ "${expected}" == "${actual}" ]]; then
        printf '  ok   %s\n' "${label}"
    else
        printf '  FAIL %s\n       expected: %s\n       actual:   %s\n' \
            "${label}" "${expected}" "${actual}"
        fail_count=$((fail_count + 1))
    fi
}

# Build the list in a subshell so each case gets a clean environment.
list_for() {
    local allowlist="$1"
    shift
    (
        # The three variables the hook API always provides.
        export AGENT_URL="http://127.0.0.1:5099"
        export MAGPILOT_AGENT_TOKEN="tok"
        export MAGPILOT_AGENT_HOME="/home/magnus"
        export MAGPILOT_BOOTSTRAP_HOOK_ENV_ALLOWLIST="${allowlist}"
        while (($#)); do
            export "${1?}"
            shift
        done
        MAGPILOT_BOOTSTRAP_LIB_ONLY=1 . "${BOOTSTRAP}"
        hook_env_list 2>/dev/null
    )
}

echo "bootstrap hook env allowlist"

check "base contract vars are always forwarded" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "")"

check "comma-separated allowlist is appended in order" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME,DEPLOYMENT_MODEL,DEPLOYMENT_MODE" \
    "$(list_for "DEPLOYMENT_MODEL,DEPLOYMENT_MODE" \
        "DEPLOYMENT_MODEL=example-model" "DEPLOYMENT_MODE=fast")"

check "whitespace-separated allowlist is accepted" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME,DEPLOYMENT_MODEL" \
    "$(list_for "  DEPLOYMENT_MODEL  " "DEPLOYMENT_MODEL=example-model")"

check "unset names are skipped" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "DEPLOYMENT_NOT_SET")"

check "empty values are still forwarded (set, not absent)" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME,DEPLOYMENT_OPTION" \
    "$(list_for "DEPLOYMENT_OPTION" "DEPLOYMENT_OPTION=")"

check "duplicates collapse" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME,DEPLOYMENT_MODEL" \
    "$(list_for "DEPLOYMENT_MODEL,DEPLOYMENT_MODEL,AGENT_URL" "DEPLOYMENT_MODEL=x")"

check "names su always resets are refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "PATH,HOME,USER,SHELL,LOGNAME,IFS")"

# Injection attempts: each of these is a value-shaped or command-shaped entry
# rather than a name, so it must be dropped rather than reaching su.
check "assignment-shaped entry is refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "DEPLOYMENT_MODEL=oops")"

check "command-shaped entry is refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for 'FOO;id')"

check "substitution-shaped entry is refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for '$(id)')"

check "hyphenated name is refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "not-a-name")"

check "leading-digit name is refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "9LIVES")"

check "wildcard entry does not pathname-expand or forward matching variables" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(cd "${SCRIPT_DIR}/.." && list_for "*" "deploy=unintended")"

check "a valid name survives alongside a rejected one" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME,DEPLOYMENT_MODEL" \
    "$(list_for "bad-name,DEPLOYMENT_MODEL" "DEPLOYMENT_MODEL=example-model")"

check "shell and loader startup controls are refused" \
    "AGENT_URL,MAGPILOT_AGENT_TOKEN,MAGPILOT_AGENT_HOME" \
    "$(list_for "BASH_ENV,ENV,SHELLOPTS,BASHOPTS,LD_PRELOAD,LD_LIBRARY_PATH,LD_AUDIT,GLIBC_TUNABLES,GCONV_PATH,LOCPATH" \
        "BASH_ENV=/untrusted" "ENV=/untrusted" \
        "LD_PRELOAD=/untrusted" "LD_LIBRARY_PATH=/untrusted" "LD_AUDIT=/untrusted" \
        "GLIBC_TUNABLES=glibc.malloc.check=3" "GCONV_PATH=/untrusted" "LOCPATH=/untrusted")"

if ((fail_count > 0)); then
    printf '\n%d check(s) failed\n' "${fail_count}"
    exit 1
fi
printf '\nall checks passed\n'
