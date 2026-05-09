#!/usr/bin/env bash
# Phase B+ port: copy LOW-risk capability files from OpenClaw to Magnus.
# Patches /home/node/ -> /home/magnus/ in scripts so paths resolve.
#
# Run with: bash phase-b-port-lowrisk.sh
set -euo pipefail

OC=/srv/openclaw/home
MG=/srv/magpilot-agent/home

mkdir -p "${MG}/magnus" "${MG}/bin" "${MG}/.config/gh" "${MG}/keys"

# --- Todo scripts (gist-backed) ---
for f in todo.py todo.mjs todo-maintenance.mjs; do
  sed 's|/home/node/|/home/magnus/|g' "${OC}/clawd/${f}" > "${MG}/magnus/${f}"
  chmod 600 "${MG}/magnus/${f}"
  echo "[port] magnus/${f}"
done
chmod 700 "${MG}/magnus/todo.py"  # python script, executable

# --- gh CLI binary + auth (classic PAT works for gh itself) ---
cp -a "${OC}/bin/gh" "${MG}/bin/gh"
chmod 755 "${MG}/bin/gh"
echo "[port] bin/gh ($(stat -c%s ${MG}/bin/gh) bytes)"

cp "${OC}/.config/gh/hosts.yml" "${MG}/.config/gh/hosts.yml"
cp "${OC}/.config/gh/config.yml" "${MG}/.config/gh/config.yml"
chmod 600 "${MG}/.config/gh/hosts.yml"
echo "[port] .config/gh/{hosts,config}.yml"

# --- github-daily-report.sh (read-only github queries) ---
sed 's|/home/node/|/home/magnus/|g' "${OC}/bin/github-daily-report.sh" > "${MG}/bin/github-daily-report.sh"
chmod 755 "${MG}/bin/github-daily-report.sh"
echo "[port] bin/github-daily-report.sh"

# --- Proxmox SSH key (for accessing the Proxmox host or LXC neighbours) ---
cp "${OC}/clawd/proxmox.json" "${MG}/magnus/proxmox.json"
cp "${OC}/clawd/proxmox_docker_key" "${MG}/magnus/proxmox_docker_key"
cp "${OC}/clawd/proxmox_docker_key.pub" "${MG}/magnus/proxmox_docker_key.pub"
chmod 600 "${MG}/magnus/proxmox.json" "${MG}/magnus/proxmox_docker_key"
chmod 644 "${MG}/magnus/proxmox_docker_key.pub"
echo "[port] magnus/proxmox.{json,_docker_key,_docker_key.pub}"

# --- Patch MEMORY.md so Magnus knows the tools are now local on him ---
# Insert a note at the top describing the port. Idempotent: only insert if
# the marker isn't already present.
MARKER="<!-- magnus-port-note -->"
if ! grep -q "${MARKER}" "${MG}/magnus/MEMORY.md"; then
  TMP=$(mktemp)
  cat > "${TMP}" <<EOF
${MARKER}
> _Capability port (2026-05-07): Magnus now has the LOW-risk capabilities
> ported from OpenClaw -- todo gist scripts, gh CLI + auth, github daily
> report, and proxmox SSH key. Paths in this file already point at
> /home/magnus/. The Outlook and Spotify integrations remain on OpenClaw
> for now (they have shared refresh tokens that we don't want to race on)._

EOF
  cat "${MG}/magnus/MEMORY.md" >> "${TMP}"
  mv "${TMP}" "${MG}/magnus/MEMORY.md"
  echo "[port] inserted port-note into MEMORY.md"
fi

# --- Fix ownership ---
chown -R 1000:1000 "${MG}"

# --- Sanity check ---
echo
echo --- runtime smoke check inside magnus container ---
docker exec magnus bash -c '
  echo --PATH-- ; echo $PATH
  echo --gh version-- ; /home/magnus/bin/gh --version 2>&1 | head -2
  echo --gh auth status-- ; GH_CONFIG_DIR=/home/magnus/.config/gh /home/magnus/bin/gh auth status 2>&1 | head -10
  echo --python3-- ; python3 --version 2>&1 || echo "python3 not present in container"
'

echo "[done] low-risk port complete"
