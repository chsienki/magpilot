#!/usr/bin/env bash
# magpilot-cron run.sh -- run a named job by posting its prompt template to
# the local agent's /api/quick-prompt, pinning the request to Magnus's
# Operations session so cron noise doesn't pollute the main thread.
#
# Pinned session id is read from the magpilot-agent's persistent state at
# /srv/magpilot-agent/home/.magnus/.operations-session-id (created by the
# Magnus bootstrap on first boot).
#
# Talks DIRECTLY to the local agent (Magnus on the same LXC), not through
# the hub. The hub's /api endpoints are gated by user OAuth and don't
# accept the shared agent bearer token.
#
# Usage:   /srv/magpilot-cron/run.sh <job-name>
# Example: /srv/magpilot-cron/run.sh magnus-heartbeat
#
# Jobs are defined in /srv/magpilot-cron/jobs.yaml. The parser uses awk
# (mawk-compatible literal-6-space match for prompt-body lines).
#
# Delivery channels:
#   log    -- write to /var/log/magpilot-cron/<job>.log (default)
#   stdout -- print to stdout (cron mails this to root)
#   whatsapp -- POST to the whatsapp sidecar's outbound endpoint (TBD)
set -euo pipefail

JOBS_FILE=/srv/magpilot-cron/jobs.yaml
LOG_DIR=/var/log/magpilot-cron
AGENT_URL="${MAGPILOT_AGENT_URL:-http://127.0.0.1:5099}"
TOKEN_FILE=/srv/magpilot/.env
OPS_SID_FILE=/srv/magpilot-agent/home/.magnus/.operations-session-id

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <job-name>" >&2
  exit 2
fi
JOB="$1"
mkdir -p "${LOG_DIR}"
LOG="${LOG_DIR}/${JOB}.log"

TOKEN=$(grep -E '^MAGPILOT_AGENT_TOKEN=' "${TOKEN_FILE}" | cut -d= -f2-)

# Operations session is the durable target. If it doesn't exist (e.g.
# first boot before bootstrap created it), fall through to ephemeral mode.
OPS_SID=""
if [[ -s "${OPS_SID_FILE}" ]]; then
  OPS_SID=$(cat "${OPS_SID_FILE}")
fi

get_block() {
  awk -v target="$1" '
    BEGIN { in_match = 0; in_prompt = 0 }
    /^[ \t]*-[ \t]*name:/ {
      sub(/^[ \t]*-[ \t]*name:[ \t]*/, "")
      gsub(/^[ \t]+|[ \t]+$/, "")
      in_match = ($0 == target)
      in_prompt = 0
      next
    }
    !in_match { next }
    /^[ \t]+prompt:[ \t]*\|/ { in_prompt = 1; print "PROMPT:"; next }
    in_prompt {
      if ($0 ~ /^      /) { sub(/^      /, ""); print "PLINE:" $0; next }
      else { in_prompt = 0 }
    }
    /^[ \t]+agent:/    { sub(/^[ \t]+agent:[ \t]*/, ""); print "AGENT:" $0 }
    /^[ \t]+delivery:/ { sub(/^[ \t]+delivery:[ \t]*/, ""); print "DELIVERY:" $0 }
    /^[ \t]+timeout:/  { sub(/^[ \t]+timeout:[ \t]*/, ""); print "TIMEOUT:" $0 }
  ' "${JOBS_FILE}"
}

block=$(get_block "${JOB}")
if [[ -z "${block}" ]]; then
  echo "[$(date -Is)] no job named '${JOB}' in ${JOBS_FILE}" | tee -a "${LOG}" >&2
  exit 3
fi
agent=$(echo "${block}" | sed -n 's/^AGENT://p' | head -n1); agent=${agent:-magnus}
delivery=$(echo "${block}" | sed -n 's/^DELIVERY://p' | head -n1); delivery=${delivery:-log}
timeout=$(echo "${block}" | sed -n 's/^TIMEOUT://p' | head -n1); timeout=${timeout:-60}
prompt=$(echo "${block}" | sed -n 's/^PLINE://p')

if [[ -n "${OPS_SID}" ]]; then
  body=$(jq -n --arg p "${prompt}" --argjson t "${timeout}" --arg s "${OPS_SID}" \
    '{prompt:$p, timeoutSeconds:$t, sessionId:$s}')
else
  body=$(jq -n --arg p "${prompt}" --argjson t "${timeout}" \
    '{prompt:$p, timeoutSeconds:$t}')
fi

started=$(date -Is)
echo "[${started}] running job=${JOB} agent=${agent} delivery=${delivery} pinned=${OPS_SID:-(none)}" >> "${LOG}"

resp=$(curl -fsS --max-time $((timeout + 30)) -X POST \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json" \
  -d "${body}" \
  "${AGENT_URL}/api/quick-prompt" 2>&1) || {
    echo "[${started}] FAIL job=${JOB}: ${resp}" >> "${LOG}"
    exit 1
}

text=$(echo "${resp}" | jq -r '.responseText // .error // .')
echo "[${started}] response (${#text} chars):" >> "${LOG}"
echo "${text}" >> "${LOG}"
echo "---" >> "${LOG}"

case "${delivery}" in
  log)    : ;;
  stdout) echo "${text}" ;;
  whatsapp)
    echo "[${started}] WARN: whatsapp delivery requested for ${JOB} but sidecar not yet deployed" >> "${LOG}"
    ;;
  *) echo "[${started}] WARN: unknown delivery '${delivery}'" >> "${LOG}" ;;
esac
