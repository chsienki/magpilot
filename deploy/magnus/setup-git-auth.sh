#!/usr/bin/env bash
# Wire up git inside Magnus container so:
#   1. git stops complaining about safe.directory ownership mismatches
#      (host UID/GID match the magnus user but git is paranoid).
#   2. HTTPS pulls of private GitHub repos succeed via the gh CLI's
#      credential helper, using the GH_CONFIG_DIR auth that
#      port-lowrisk.sh planted.
set -euo pipefail

docker exec --user magnus magnus bash -lc '
set -e
git config --global --add safe.directory /home/magnus/copilot-context
git config --global --add safe.directory "/home/magnus/.copilot/installed-plugins/chsienki/task-context"
git config --global --add safe.directory "*"
# Tell git to ask gh for HTTPS creds. gh stores them in ~/.config/gh/hosts.yml
# which is already populated.
export GH_CONFIG_DIR=/home/magnus/.config/gh
/home/magnus/bin/gh auth setup-git
echo "--- gitconfig ---"
cat ~/.gitconfig
echo "--- test pull ---"
git -C /home/magnus/copilot-context pull --quiet && echo "PULL OK" || echo "PULL FAIL"
'
