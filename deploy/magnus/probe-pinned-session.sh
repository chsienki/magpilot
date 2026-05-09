#!/usr/bin/env bash
# Probe Magnus's pinned session: send a quick-prompt that asks for `pwd` and
# the contents of IDENTITY.md. Verifies cwd is correctly /home/magnus/magnus
# and that the working files survived the rename.
set -euo pipefail

cd /srv/magpilot-agent
TOKEN=$(grep '^MAGPILOT_AGENT_TOKEN' .env | cut -d= -f2)
SID="${1:-ca7710c1-dcda-4de3-a0e7-c42e2af895c2}"

echo "[probe] sid=$SID"
curl -fsS -X POST \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Content-Type: application/json" \
    -d "{\"prompt\":\"Reply briefly: what does \\\`pwd\\\` say, and does ./IDENTITY.md exist?\",\"sessionId\":\"${SID}\",\"timeoutSeconds\":120}" \
    http://127.0.0.1:5099/api/quick-prompt
echo
