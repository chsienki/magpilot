# TUI characterization matrix

This tool characterizes Copilot's terminal UI as a black box. It launches the
local Magpilot wrapper with controlled combinations of terminal hints, captures
the first settled seconds of ANSI output, and renders each capture into a
normalized terminal screen for comparison.

## Setup

```pwsh
npm ci --prefix .\tools\tui-matrix
```

## Capture

Run from the magpilot repository root:

```pwsh
# Show the generated cases without building or launching anything.
./scripts/capture-tui-matrix.ps1 -Phase Hints -ListCases

# Full 16-combination hint matrix plus forced dark/light background cases.
./scripts/capture-tui-matrix.ps1 -Phase Hints

# Isolate Magpilot's palette, thinking, input-band, legacy-color, and banner stages.
./scripts/capture-tui-matrix.ps1 -Phase Rendering

# Compare PTY-none with palette replies, device attributes, and both proxied.
./scripts/capture-tui-matrix.ps1 -Phase Transport

# Run selected cases only.
./scripts/capture-tui-matrix.ps1 -Phase All -Case none,truecolor,background,all
```

Each case is interactive. Wait for Copilot's welcome screen to settle, note
what changed, then type `/exit`. The default capture window is the first 3000
milliseconds after Copilot emits its first output byte; override it with
`-CaptureMilliseconds`.

The runner removes `TERM`, `COLORTERM`, `COLORFGBG`,
`COPILOT_GITHUB_THEME`, and `MAGPILOT_AGENT_TOKEN` from its child environment.
The diagnostic dump itself forces the local wrapper's PTY passthrough even for
the `none` case, so every capture has identical hosting while only the selected
TUI options vary. This means `none` is a **PTY-none** baseline, not the
agentless direct-exec path used by a normal `--magpilot-no-tui-changes` launch.
The byte-capture pipeline cannot observe direct-exec output without
interposing a PTY and changing the condition being measured. Compare the
direct/raw composer visually; use the matrix only to compare transformations
within the PTY-hosted path.

Copilot queries the terminal palette and capabilities during startup. PTY
captures include `OSC 4;<index>;?` for all 16 ANSI slots plus synchronized
output and keyboard-protocol mode queries. A UI difference that persists in
every PTY case but is absent in direct-exec is therefore outside the TUI option
matrix: investigate the query/reply path through the outer shell and ConPTY.

When launching from Git Bash, invoke the PowerShell runner explicitly and pass
selected cases as one comma-separated argument:

```bash
cd /path/to/magpilot
npm ci --prefix ./tools/tui-matrix
pwsh -NoProfile -File ./scripts/capture-tui-matrix.ps1 \
  -Phase All \
  -Case 'none,truecolor,background,term+truecolor+background+github-theme,all'
```

The manifest records `MSYSTEM`, `SHELL`, `TERM_PROGRAM`, `WT_SESSION`, and
whether .NET considered stdin/stdout redirected. Keep Git Bash and PowerShell
captures in separate output directories; they are different terminal-hosting
experiments even when the TUI option list is identical.

Use `-ThemeFile <path>` for rendering cases. When omitted, the runner first
honors `MAGPILOT_TERM_THEME_FILE` or `MAGPILOT_TERM_THEME` inherited from the
invoking shell, then falls back to
`%ProgramFiles%\Magpilot\config\magpilot.env`.

## Outputs

Each case directory contains:

| File | Purpose |
|---|---|
| `manifest.json` | Enabled options, terminal dimensions, resolved child environment, background result, and which transformations activated |
| `raw.ansi` | Copilot output before Magpilot's byte-stream rewrite |
| `post.ansi` | Bytes actually sent to the outer terminal |
| `raw-screen.txt` / `raw-screen.json` | Normalized terminal state produced by Copilot before Magpilot processing |
| `screen.txt` | Readable settled terminal text |
| `screen.json` | Settled cell grid represented as text plus style runs |
| `diff-raw-vs-post.json` | Rows changed by Magpilot processing within this case |
| `diff-vs-none.json` | Rows whose text or cell styling differs from the `none` baseline |

The capture root also contains `summary.csv` for byte-level hashes and
`screens-summary.csv` for normalized visual hashes and changed-row counts.

Interpret comparisons in two stages:

1. Differences between cases in `raw-screen.json` show Copilot reacting to
   terminal hints.
2. Differences in `diff-raw-vs-post.json` come from Magpilot's rewrite or
   banner stages. Palette changes use OSC sequences against the outer terminal
   and are recorded in the manifest rather than resolved to RGB by the
   headless renderer.

Keep the Copilot version, selected `/theme`, terminal emulator, window size,
working directory, and capture duration fixed across a matrix.

The `Transport` phase keeps all visual TUI options disabled and compares the
two Porta.Pty ConPTY implementations:

| Case | ConPTY implementation |
|---|---|
| `none` | Current app-local `conpty.dll` + `OpenConsole.exe` |
| `system-none` | Windows' in-box `kernel32!CreatePseudoConsole` |

The app-local path should match the direct terminal's query replies; the
system path remains available as a diagnostic fallback.

## Offline theme lab

Start the local editor from the magpilot repository root:

```pwsh
npm run editor --prefix .\tools\tui-matrix
```

Open `http://127.0.0.1:5178`. The editor discovers capture cases under
`tools\tui-matrix\captures`, preferring a capture whose manifest contains a
theme. It replays `raw.ansi` through xterm.js and provides:

- Default foreground/background and all 16 ANSI palette slots.
- Optional `thinking`, `inputBand`, and `legacyDefaultColors` controls using
  the same mappings as `AnsiColorRewriter`.
- Click inspection for foreground/background selector, attributes, and the
  number of visible cells sharing that selector.
- A selector summary showing fixed truecolor, ANSI, and faint usage in the
  capture.
- Preview controls for bold-to-bright promotion and xterm.js minimum contrast.
- Theme JSON import and export.

The selected capture and most recently loaded/edited theme persist in browser
local storage, so refreshing the editor does not revert to the capture theme.

The editor is intentionally offline: it never owns a live PTY or sends input
to Copilot. Changes rerender the recorded stream in the browser, making rapid
color iteration safe and deterministic. A palette selector is shared; editing
ANSI slot 5, for example, updates every normal-magenta cell. The thinking
control carries a warning because it affects every faint (`SGR 2`) span, not
only reasoning text.

The browser preview is selector-accurate, not pixel-identical. xterm.js and
Windows Terminal use the requested RGB values but differ in font rasterization
and faint-text blending. Captures can also contain fixed truecolor tokens that
bypass ANSI palette slots; use the selector summary and cell inspector to see
which control owns a visible element.

Copilot can query the terminal palette and then emit the returned values as
fixed truecolor. The editor uses the capture manifest to recognize those
palette-derived RGB values and remap them to the corresponding slot in the
edited theme. This lets changing bright magenta, for example, update a captured
loading line even when its ANSI stream contains the old palette value as RGB.

## Direct versus nested terminal probe

The capture matrix always uses a PTY, so it cannot diagnose differences between
raw/direct Copilot and the nested Magpilot ConPTY path. Run this from the Git
Bash window where the composer differs:

```bash
pwsh -NoProfile -File ./scripts/probe-terminal-paths.ps1
```

The script runs the same Node probe directly and through Porta.Pty's app-local
and system ConPTY implementations. It records:

- Node's `stdin/stdout.isTTY`, dimensions, color depth, and `hasColors` results.
- Win32 input/output handle types, console modes, code pages, and whether
  `ENABLE_VIRTUAL_TERMINAL_INPUT` is active after Node enters raw mode.
- Replies to OSC palette/foreground/background queries.
- Device attributes, window size, synchronized-output, keyboard-protocol, and
  terminal-status mode replies.

Results are written under `tools/tui-matrix/captures/terminal-probe-*`.
`comparison.json` contains both complete observations and a compact direct
versus nested difference section.

On the Git Bash/Windows Terminal setup used to develop Magpilot, Porta.Pty's
app-local ConPTY matches the direct terminal's TTY flags, dimensions, color
depth, palette replies, device attributes, mode reports, console modes, and
code pages. The system/in-box ConPTY remains the reduced diagnostic control.

The original mismatch was measured against Pty.Net's bundled January 2022
runtime. Magpilot now targets .NET 10 and uses Porta.Pty 2.2.2. Its correctly
staged app-local path matches the direct Git Bash terminal's palette, device,
mode, TTY, size, and console-mode observations; the system path remains
different. Rerun the probe after any PTY/runtime change.
