#!/usr/bin/env bash
# One-shot: fix the double-nesting introduced by phase-b-setup.sh's
# cp -a /srv/openclaw/home/copilot-context /srv/magpilot-agent/home/copilot-context.
# That landed the inner clone at /home/magnus/copilot-context/copilot-context
# instead of /home/magnus/copilot-context. Flatten it.
set -euo pipefail
cd /srv/magpilot-agent/home
if [[ -d copilot-context/copilot-context/.git && ! -d copilot-context/.git ]]; then
    echo "fixing double-nest..."
    mv copilot-context copilot-context.OLD
    mv copilot-context.OLD/copilot-context copilot-context
    rmdir copilot-context.OLD
    chown -R 1000:1000 copilot-context
    echo "done. structure:"
    ls -la copilot-context | head -8
else
    echo "already correct or unexpected layout:"
    ls -la copilot-context | head -8
fi
