#!/usr/bin/env bash
# One-shot migration: rename Magnus's working dir from "clawd" -> "magnus" and
# rewrite the two pinned sessions' workspace.yaml + events.jsonl so ACP can
# still session/load them after the rename. Run as root inside LXC 102.
set -euo pipefail

cd /srv/magpilot-agent

if [[ ! -d home/clawd ]]; then
    if [[ -d home/magnus ]]; then
        echo "[rename] home/clawd already renamed (home/magnus exists). Continuing with session rewrites in case they're stale."
    else
        echo "[rename] FATAL: neither home/clawd nor home/magnus exists" >&2
        exit 1
    fi
else
    echo "[rename] stopping magnus container"
    docker compose stop magnus

    echo "[rename] renaming home/clawd -> home/magnus"
    mv home/clawd home/magnus
fi
ls -ld home/magnus

SS=home/.copilot/session-state
echo "[rename] rewriting pinned-session workspace.yaml + events.jsonl"
for sid in ca7710c1-dcda-4de3-a0e7-c42e2af895c2 cc9708fe-bb7b-45ff-a038-36343a11a8c3; do
    echo "  --- $sid ---"
    for f in workspace.yaml events.jsonl; do
        if [[ -f "$SS/$sid/$f" ]]; then
            before=$(grep -c '/home/magnus/clawd' "$SS/$sid/$f" || true)
            sed -i 's|/home/magnus/clawd|/home/magnus/magnus|g' "$SS/$sid/$f"
            after=$(grep -c '/home/magnus/clawd' "$SS/$sid/$f" || true)
            echo "    $f: replaced ${before} refs (remaining=${after})"
        fi
    done
done

echo "[rename] rewriting container's copilot-instructions.md"
if [[ -f home/.copilot/copilot-instructions.md ]]; then
    sed -i 's|~/clawd/|~/magnus/|g; s|/home/magnus/clawd/|/home/magnus/magnus/|g' \
        home/.copilot/copilot-instructions.md
fi

echo "[rename] all references rewritten. ready to start magnus with the new image."
ls home/ | head
