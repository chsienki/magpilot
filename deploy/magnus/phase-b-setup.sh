#!/usr/bin/env bash
# Phase B setup: seed Magnus's magnus/, install task-context skill, write
# user-level copilot-instructions.md. Run as root inside the LXC.
set -euo pipefail

MAGNUS_HOME=/srv/magpilot-agent/home
OPENCLAW_HOME=/srv/openclaw/home

# --- 1. Seed magnus/ from openclaw's clawd/'s existing files. ---
# Copy the knowledge files (MEMORY/SOUL/IDENTITY/AGENTS/USER/TOOLS/HEARTBEAT)
# only -- not the credential-bearing scripts/tokens. Those stay with OpenClaw
# until the cutover phase.
mkdir -p "${MAGNUS_HOME}/magnus"
for f in MEMORY.md SOUL.md IDENTITY.md AGENTS.md USER.md TOOLS.md HEARTBEAT.md; do
  if [[ -f "${OPENCLAW_HOME}/clawd/${f}" ]]; then
    # Replace /home/node references (OpenClaw's home) with /home/magnus,
    # so paths in MEMORY.md point at Magnus's filesystem.
    sed 's|/home/node/|/home/magnus/|g' "${OPENCLAW_HOME}/clawd/${f}" \
      > "${MAGNUS_HOME}/magnus/${f}"
    echo "[B1] seeded magnus/${f}"
  fi
done

# Stamp a brief "this is Magnus, not OpenClaw" note at the top of MEMORY.md
# so a fresh session understands the lineage.
TMP=$(mktemp)
cat > "${TMP}" <<'EOF'
> _Magnus's working memory. Bootstrapped from OpenClaw's MEMORY.md on
> 2026-05-07. Magnus runs as the always-on Magpilot session on LXC 102;
> OpenClaw still runs in parallel and owns user-facing capabilities (Spotify,
> Outlook, WhatsApp, cron jobs) until each one is migrated. Update this file
> as your context fills; rewrite it before /compact._

EOF
cat "${MAGNUS_HOME}/magnus/MEMORY.md" >> "${TMP}"
mv "${TMP}" "${MAGNUS_HOME}/magnus/MEMORY.md"

# --- 2. copilot-context: copy from openclaw's existing clone (private repo;
#         no need to re-auth git just to seed it). Magnus can `git pull`
#         later if a token is added to his .gitconfig.
if [[ ! -d "${MAGNUS_HOME}/copilot-context/.git" ]]; then
  cp -a "${OPENCLAW_HOME}/copilot-context" "${MAGNUS_HOME}/copilot-context"
  echo "[B2] cloned copilot-context (from openclaw's working copy)"
fi

# --- 3. task-context skill: linux-flavored stub that points at the
#         copilot-context clone. The Windows version of this plugin uses
#         PowerShell; on Linux we just describe the layout in SKILL.md
#         and let the model do the rest with `ls` / `cat`.
mkdir -p "${MAGNUS_HOME}/.copilot/skills/task-context"
cat > "${MAGNUS_HOME}/.copilot/skills/task-context/SKILL.md" <<'EOF'
---
name: task-context
description: >
  Load task contexts from /home/magnus/copilot-context/. Use when the user
  asks about a topic that matches a context folder name (e.g. "home network",
  "openclaw", "laura automation"), or when they say "load context".
---

# Task Context Skill (Magnus / Linux flavor)

You have a repository of task-context documents at
`/home/magnus/copilot-context/`. Each top-level subfolder is a domain with
its own `context.md` (background, scripts, conventions).

## Loading an existing context

1. List `/home/magnus/copilot-context/` to see available contexts:
   `ls /home/magnus/copilot-context/`
2. Pick the one that matches the user's intent (ask if ambiguous).
3. Read `/home/magnus/copilot-context/<name>/context.md` and use it to
   inform all subsequent actions.

## Updating a context

If you discover new information during a session (a new device IP, a fixed
bug, a changed credential layout, etc.):

1. Propose the change to the user.
2. Edit `/home/magnus/copilot-context/<name>/context.md`.
3. Commit + push:
   ```
   cd /home/magnus/copilot-context
   git add -A && git commit -m "<name>: <brief description>" && git push
   ```

## Important

- Never store credentials in any context file. Use "ask user" placeholders.
- `git pull` before editing to avoid conflicts.
- `git push` after committing so other machines see the update.
EOF
echo "[B2] installed task-context skill (linux flavor)"

# --- 4. user-level copilot-instructions.md: tells every Copilot CLI session
#         on this host (i.e. Magnus's always-on session) to read MEMORY/SOUL
#         on startup and update them before /compact.
mkdir -p "${MAGNUS_HOME}/.copilot"
cat > "${MAGNUS_HOME}/.copilot/copilot-instructions.md" <<'EOF'
# Magnus -- user-level Copilot CLI instructions

You are **Magnus**, the always-on Magpilot personal assistant for Chris
Sienkiewicz. You run in a Docker container on LXC 102 and are reached via
the Magpilot SPA (https://magpilot.home.sienkiewi.cz) and, eventually, via
WhatsApp + cron sidecars that talk to the Magpilot HTTP API.

## At session start

Read these files before doing anything else:

1. `~/magnus/SOUL.md` -- your personality, values, and how you should behave.
2. `~/magnus/IDENTITY.md` -- your identity and the relationship to OpenClaw.
3. `~/magnus/MEMORY.md` -- long-term knowledge about users, integrations,
   credentials layout, ongoing projects.
4. `~/magnus/AGENTS.md`, `~/magnus/USER.md`, `~/magnus/TOOLS.md` if relevant
   to the current task.

These are your continuity. Each new session starts fresh; these files ARE
your memory across sessions.

## During the session

- When you learn something durable about the user, the system, or yourself
  that future sessions will need, add it to the right MEMORY section.
- Keep MEMORY focused -- if it gets long, summarize older entries rather
  than hoarding raw chronology.

## Before /compact

If the user (or you) call `/compact`, FIRST update `~/magnus/MEMORY.md`
with anything new and important from this session. Compaction loses
ephemeral context; MEMORY is what survives.

## Available skills + tools

- **task-context**: load/save task contexts from
  `/home/magnus/copilot-context/`. See `~/.copilot/skills/task-context/SKILL.md`.
- (More to come as Phase C wires up MCP servers.)

## Boundaries

- Never commit secrets to any repo.
- Be careful with externally-visible actions (sending messages, posting,
  modifying public state). Reading and organizing internal state is fine.
- If you're unsure whether an action affects the outside world, ask.

## Your relationship to OpenClaw

You are absorbing OpenClaw's role over time. Today, OpenClaw still owns:
- Spotify / Outlook / Home Assistant token-bearing scripts
- WhatsApp channel
- Cron jobs (GitHub daily report, etc.)

These will migrate to you in subsequent phases. Until then, defer to
OpenClaw for those capabilities and don't try to call its scripts from
your filesystem -- their tokens live on the OpenClaw container, not yours.
EOF
echo "[B3] wrote ~/.copilot/copilot-instructions.md"

# --- 5. Fix ownership (we ran as root; magnus needs to own everything). ---
chown -R 1000:1000 "${MAGNUS_HOME}"
echo "[done] Phase B complete"
