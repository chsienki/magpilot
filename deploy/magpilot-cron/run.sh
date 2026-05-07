#!/usr/bin/env bash
# magpilot-cron run.sh -- run a named job by posting its prompt template to
# the Magpilot hub's quick-prompt endpoint, then deliver the response.
#
# Usage:   /srv/magpilot-cron/run.sh <job-name>
# Example: /srv/magpilot-cron/run.sh magnus-heartbeat
#
# Jobs are defined in /srv/magpilot-cron/jobs.yaml. A YAML parser would be
# nice, but to avoid a python/yq dependency for now we keep jobs.yaml as a
# tiny key/value file and parse with awk. See jobs.yaml for the format.
#
# Delivery channels:
#   log    -- write to /var/log/magpilot-cron/<job>.log (default)
#   stdout -- print to stdout (cron mails this to root)
#   whatsapp -- POST to the whatsapp sidecar's outbound endpoint
#               (Phase E -- not yet implemented)
set -euo pipefail

JOBS_FILE=/srv/magpilot-cron/jobs.yaml
LOG_DIR=/var/log/magpilot-cron
# magpilot-cron talks DIRECTLY to the local agent (Magnus on the same LXC),
# not through the hub. The hub's /api endpoints are gated by user OAuth and
# don't accept the shared agent bearer token. Talking direct keeps cron
# self-sufficient and avoids needing a service-account auth concept.
AGENT_URL="${MAGPILOT_AGENT_URL:-http://127.0.0.1:5099}"
TOKEN_FILE=/srv/magpilot/.env

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <job-name>" >&2
  exit 2
fi
JOB="$1"
mkdir -p "${LOG_DIR}"
LOG="${LOG_DIR}/${JOB}.log"

TOKEN=$(grep -E '^MAGPILOT_AGENT_TOKEN=' "${TOKEN_FILE}" | cut -d= -f2-)

# Tiny YAML extractor: looks for a job block whose name matches $JOB and
# emits the prompt + delivery + agent + timeout fields. Format is rigid:
#   - name: magnus-heartbeat
#     agent: magnus
#     timeout: 60
#     delivery: log
#     prompt: |
#       <prompt body indented exactly 6 spaces>
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
      # Prompt body lines must be indented at least 6 spaces. Use literal
      # 6-space match (mawk on Debian does NOT support {6,} intervals).
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

body=$(jq -n --arg p "${prompt}" --argjson t "${timeout}" \
  '{prompt:$p, timeoutSeconds:$t}')

started=$(date -Is)
echo "[${started}] running job=${JOB} agent=${agent} delivery=${delivery}" >> "${LOG}"

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
