#!/usr/bin/env bash
# magpilot-shim Phase 1 acceptance test
# ============================================================================
#
# Exercises the new agent endpoints introduced for the cooperative
# single-owner handoff between magpilot-host and magpilot-agent. Run this
# against a local agent (defaults to http://127.0.0.1:5099) before declaring
# Phase 1 done.
#
# Tests (in order):
#   1. GET /state  on a fresh agent-owned session         -> owner=Agent
#   2. Send a prompt + poll /state during turn            -> activity=InFlight
#   3. POST /release-request                              -> 202 Accepted
#      (subscribed SSE in background captures release_requested event)
#   4. POST /acquire-for-host                             -> owner=Host, our PID
#   5. POST /acquire-for-host AGAIN with same PID         -> idempotent OK
#   6. POST /release with WRONG host PID                  -> 409 Conflict
#   7. POST /release with correct host PID                -> owner=Agent again
#   8. Cleanup: detach + delete temp cwd
#
# Usage:
#   MAGPILOT_AGENT_TOKEN=... ./scripts/test-shim-phase1.sh
#   MAGPILOT_AGENT_URL=http://...:5099 MAGPILOT_AGENT_TOKEN=... ./scripts/test-shim-phase1.sh
# ============================================================================

set -euo pipefail

URL="${MAGPILOT_AGENT_URL:-http://127.0.0.1:5099}"
TOK="${MAGPILOT_AGENT_TOKEN:-}"
if [[ -z "$TOK" ]]; then
    echo "ERROR: MAGPILOT_AGENT_TOKEN env var not set" >&2
    exit 2
fi

H_AUTH=(-H "Authorization: Bearer $TOK")
H_JSON=(-H "Content-Type: application/json")

# Pretty pass/fail markers.
PASS="\033[32m✓\033[0m"
FAIL="\033[31m✗\033[0m"
DIM="\033[2m"
RST="\033[0m"
fail_count=0

check() {
    local name="$1" got="$2" want="$3"
    if [[ "$got" == "$want" ]]; then
        echo -e "  $PASS $name ${DIM}($got)$RST"
    else
        echo -e "  $FAIL $name: got '$got', want '$want'"
        fail_count=$((fail_count + 1))
    fi
}

contains() {
    local name="$1" haystack="$2" needle="$3"
    if [[ "$haystack" == *"$needle"* ]]; then
        echo -e "  $PASS $name ${DIM}(contains '$needle')$RST"
    else
        echo -e "  $FAIL $name: '$haystack' did not contain '$needle'"
        fail_count=$((fail_count + 1))
    fi
}

# Use a per-run temp cwd so we don't pollute anything. On Git Bash on
# Windows, the agent expects native Windows paths (the agent runs as a
# native Win32 process), so convert via cygpath if it's available.
TEST_CWD="${TMPDIR:-/tmp}/magpilot-shim-phase1-$$"
mkdir -p "$TEST_CWD"
trap 'rm -rf "$TEST_CWD"' EXIT
if command -v cygpath >/dev/null 2>&1; then
    AGENT_CWD=$(cygpath -w "$TEST_CWD")
else
    AGENT_CWD="$TEST_CWD"
fi

# ----------------------------------------------------------------------------
echo "=== test-shim-phase1: agent at $URL ==="
echo

# PID we'll register with the agent as the "host". By default this is
# the script's own PID ($$). On Linux/macOS that's a real OS PID and
# the agent's HostOwnership liveness check works. On Windows under
# Git-Bash/MSYS2, $$ is an MSYS-internal PID that the Win32 process
# table doesn't know about, so the agent's Process.GetProcessById fails
# and HostOwnership immediately prunes our entry as "stale". Override
# via TEST_HOST_PID for that case (e.g., the parent PowerShell PID, the
# agent's own PID, or any other definitely-alive Win32 PID).
HOST_PID="${TEST_HOST_PID:-$$}"
echo "  using host PID: $HOST_PID (override via TEST_HOST_PID)"
echo

echo "[setup] creating a fresh session in $AGENT_CWD ..."
ESCAPED_CWD="${AGENT_CWD//\\/\\\\}"
CREATE_BODY="{\"cwd\":\"$ESCAPED_CWD\",\"name\":null,\"useAgency\":false}"
SID=$(curl -sf "${H_AUTH[@]}" "${H_JSON[@]}" -X POST -d "$CREATE_BODY" \
    "$URL/api/sessions" | python3 -c "import sys,json;print(json.load(sys.stdin)['id'])")
echo "  sid: $SID"
echo

# ----------------------------------------------------------------------------
echo "[1] GET /state on a fresh agent-owned session"
STATE=$(curl -sf "${H_AUTH[@]}" "$URL/api/sessions/$SID/state")
OWNER=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['owner'])")
ACTIVITY=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['activity'])")
check "owner is Agent"           "$OWNER"    "Agent"
check "activity is Idle"         "$ACTIVITY" "Idle"
# lastEvent CAN be null on a brand-new session before any event fires;
# we don't assert it here. We'll re-check after the prompt below.
echo

# ----------------------------------------------------------------------------
echo "[2] send a prompt + poll /state during the turn"
curl -sf "${H_AUTH[@]}" "${H_JSON[@]}" -X POST \
    -d '{"Text":"Reply with exactly: HOSTSHIM_PROBE_OK"}' \
    "$URL/api/sessions/$SID/messages" >/dev/null
echo "  prompt sent (202 fire-and-forget)"
sleep 1
STATE=$(curl -sf "${H_AUTH[@]}" "$URL/api/sessions/$SID/state")
ACTIVITY=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['activity'])")
INFLIGHT=$(echo "$STATE" | python3 -c "import sys,json;d=json.load(sys.stdin); print('yes' if d.get('inFlight') else 'no')")
check "activity during turn"     "$ACTIVITY" "InFlight"
check "inFlight info populated"  "$INFLIGHT" "yes"
# wait for the turn to actually finish before continuing
echo "  waiting for turn to complete..."
for _ in $(seq 1 60); do
    A=$(curl -sf "${H_AUTH[@]}" "$URL/api/sessions/$SID/state" | python3 -c "import sys,json;print(json.load(sys.stdin)['activity'])")
    [[ "$A" == "Idle" ]] && break
    sleep 1
done
echo "  turn done (activity=$A)"
echo

# ----------------------------------------------------------------------------
echo "[3] POST /release-request -> SSE broadcast"
# Subscribe to the SSE stream in background, then fire the request, then
# capture the event we observed. We give the subscriber 2s to register
# before posting so we don't race the broadcast.
SSE_OUT=$(mktemp)
curl -sN "${H_AUTH[@]}" --max-time 8 "$URL/api/sessions/$SID/stream" > "$SSE_OUT" 2>/dev/null &
SSE_PID=$!
sleep 2
RR_HTTP=$(curl -s -o /dev/null -w "%{http_code}" "${H_AUTH[@]}" "${H_JSON[@]}" -X POST \
    -d '{"Requester":"phase1-test","Force":false}' \
    "$URL/api/sessions/$SID/release-request")
check "release-request returns 202" "$RR_HTTP" "202"
sleep 3
# kill the SSE subscriber so the file is final
kill "$SSE_PID" 2>/dev/null || true
sleep 0.5
contains "SSE captured release_requested event" "$(cat $SSE_OUT)" '"type":"release_requested"'
contains "release_requested has Requester field" "$(cat $SSE_OUT)" '"Requester":"phase1-test"'
# Now check lastEvent works after some events have fired
STATE=$(curl -sf "${H_AUTH[@]}" "$URL/api/sessions/$SID/state")
HAS_LAST=$(echo "$STATE" | python3 -c "import sys,json;d=json.load(sys.stdin); print('yes' if d.get('lastEvent') else 'no')")
check "lastEvent populated after turn"  "$HAS_LAST" "yes"
rm -f "$SSE_OUT"
echo

# ----------------------------------------------------------------------------
echo "[4] POST /acquire-for-host -> owner becomes Host"
ACQ_BODY="{\"HostPid\":$HOST_PID,\"Force\":false}"
STATE=$(curl -sf "${H_AUTH[@]}" "${H_JSON[@]}" -X POST -d "$ACQ_BODY" \
    "$URL/api/sessions/$SID/acquire-for-host")
OWNER=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['owner'])")
GOT_PID=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin).get('hostPid',''))")
check "owner is Host after acquire"  "$OWNER"   "Host"
check "hostPid matches our PID"      "$GOT_PID" "$HOST_PID"
echo

# ----------------------------------------------------------------------------
echo "[5] POST /acquire-for-host again with same PID is idempotent"
STATE=$(curl -sf "${H_AUTH[@]}" "${H_JSON[@]}" -X POST -d "$ACQ_BODY" \
    "$URL/api/sessions/$SID/acquire-for-host")
OWNER=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['owner'])")
check "owner still Host after re-acquire" "$OWNER" "Host"
echo

# ----------------------------------------------------------------------------
echo "[6] POST /release with WRONG hostPid -> 409 Conflict"
WRONG_BODY='{"HostPid":1}'
WRONG_HTTP=$(curl -s -o /dev/null -w "%{http_code}" "${H_AUTH[@]}" "${H_JSON[@]}" -X POST \
    -d "$WRONG_BODY" "$URL/api/sessions/$SID/release")
check "release with wrong PID rejected" "$WRONG_HTTP" "409"
echo

# ----------------------------------------------------------------------------
echo "[7] POST /release with correct hostPid -> owner Agent again"
REL_BODY="{\"HostPid\":$HOST_PID}"
STATE=$(curl -sf "${H_AUTH[@]}" "${H_JSON[@]}" -X POST -d "$REL_BODY" \
    "$URL/api/sessions/$SID/release")
OWNER=$(echo "$STATE" | python3 -c "import sys,json;print(json.load(sys.stdin)['owner'])")
check "owner is Agent after release" "$OWNER" "Agent"
echo

# ----------------------------------------------------------------------------
echo "[8] cleanup: detach the test session"
curl -sf "${H_AUTH[@]}" -X POST "$URL/api/sessions/$SID/detach" >/dev/null || true
echo "  detached"
echo

# ----------------------------------------------------------------------------
echo "=== summary ==="
if [[ $fail_count -eq 0 ]]; then
    echo -e "$PASS all tests passed"
    exit 0
else
    echo -e "$FAIL $fail_count tests failed"
    exit 1
fi
