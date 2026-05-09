#!/usr/bin/env bash
# Ask Magnus to invoke the task-context skill end-to-end:
# run the pwsh script, then summarize the result. This validates that
# pwsh is on PATH, the plugin's scripts are reachable, and the
# context-repos.json is wired correctly.
set -euo pipefail
TOKEN=$(grep '^MAGPILOT_AGENT_TOKEN' /srv/magpilot-agent/.env | cut -d= -f2)
SID="${1:-ca7710c1-dcda-4de3-a0e7-c42e2af895c2}"
curl -sS -X POST \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Content-Type: application/json" \
    -d "{\"prompt\":\"Use the task-context skill: invoke list-contexts.ps1 (use pwsh) and summarize the output briefly. Don't load any context, just confirm the skill works and list the context names you find.\",\"sessionId\":\"${SID}\",\"timeoutSeconds\":300}" \
    http://127.0.0.1:5099/api/quick-prompt
echo
