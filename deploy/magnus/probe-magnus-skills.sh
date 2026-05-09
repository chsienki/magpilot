#!/usr/bin/env bash
# Ask the Magnus pinned session to enumerate the skills it sees.
set -euo pipefail
TOKEN=$(grep '^MAGPILOT_AGENT_TOKEN' /srv/magpilot-agent/.env | cut -d= -f2)
SID="${1:-ca7710c1-dcda-4de3-a0e7-c42e2af895c2}"
curl -sS -X POST \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Content-Type: application/json" \
    -d "{\"prompt\":\"List the names of skills you have access to right now. Just names, comma-separated, brief.\",\"sessionId\":\"${SID}\",\"timeoutSeconds\":180}" \
    http://127.0.0.1:5099/api/quick-prompt
echo
