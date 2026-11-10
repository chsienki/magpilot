# Terminal styling and diagnostics

The launcher can influence Copilot's TUI through environment hints, terminal
palette overrides, and output rewrites. This directory contains capture
rendering and an offline theme editor; the interactive runners live in
`scripts`.

These are **Windows terminal diagnostics**, not the browser UI or agent SDK
runtime. Use a real interactive terminal, PowerShell 7, the .NET 10 SDK,
Node.js/npm, and an installed, authenticated Copilot CLI.

## Control launcher styling

```pwsh
magpilot --magpilot-no-tui-changes
magpilot --magpilot-tui-options=term,truecolor,background,palette
```

`--magpilot-no-tui-changes` is an alias for `--magpilot-tui-options=none`.
It leaves inherited environment variables and Copilot output untouched by
Magpilot's visual transformations. Agent-coordinated sessions still use a PTY
for ownership detection and graceful handoff. Without coordination, a normal
`none` launch directly executes Copilot; diagnostic dumps are an exception
because capturing output requires a PTY.

The positive option list accepts:

| Option | Effect |
|---|---|
| `term` | Default a missing PTY child `TERM` to `xterm-256color` |
| `truecolor` | Default a missing PTY child `COLORTERM` to `truecolor` |
| `background` | Probe or pin dark/light and supply the child hint |
| `github-theme` | Enable Copilot's GitHub theme option |
| `palette` | Apply configured palette/foreground/background overrides and answer matching PTY queries |
| `thinking` | Rewrite faint (`SGR 2`) output to the configured color |
| `input-band` | Retarget recognized composer-surface truecolors |
| `legacy-colors` | Retarget known fallback color ramps |
| `banner` | Append the launcher version tag to the startup banner |

`rewrite` groups `thinking,input-band,legacy-colors`. `all` is the default;
`all` and `none` must each stand alone. Unknown values, repeated option-list
flags, or combining a list with `--magpilot-no-tui-changes` are rejected.

Rewrites require the PTY output stream. In a direct-exec path, only applicable
environment hints and palette overrides can affect Copilot; there is no
rewritable byte stream. Operational ownership messages are not visual
transformations and remain visible.

### Theme configuration

Set launcher settings in the process environment or installed `magpilot.env`;
process values win.

| Variable | Values or purpose |
|---|---|
| `MAGPILOT_TERM_BACKGROUND` | `auto` (default), `dark`, or `light` |
| `MAGPILOT_TERM_ENABLE_GITHUB_THEME` | `0` disables the default GitHub-theme hint |
| `MAGPILOT_TERM_THEME` | Theme name under `<install>\config\themes\<name>.json` |
| `MAGPILOT_TERM_THEME_FILE` | Explicit theme JSON path; takes precedence over a name |
| `MAGPILOT_TERM_BANNER_TAG` | Custom banner suffix; `0`/`off`/`false`/`none`/empty suppresses it |
| `MAGPILOT_CONPTY` | `app-local` (default) or diagnostic `system`/`inbox` |

A theme file can contain:

```json
{
  "palette": {
    "0": "#101018",
    "4": "#3b8eea",
    "5": "#b48ead"
  },
  "foreground": "#d4d4d4",
  "background": "#101018",
  "thinking": "#8fa8a8",
  "inputBand": "#18232e",
  "legacyDefaultColors": false
}
```

Palette indices and colors are validated by
[TerminalThemeConfig](../../src/Magpilot.Host/TerminalThemeConfig.cs).
The editor exposes the base 16 slots. Fixed truecolor output does not
automatically follow an ANSI palette.

The faint rewrite affects **every** `SGR 2` span, including chrome, not only
reasoning. Input-band and legacy mappings match known Copilot colors rather
than discovering arbitrary semantic roles. `legacyDefaultColors=true` also
suppresses banner insertion because animated redraws cannot account for
inserted text. A custom banner suffix is inserted verbatim; include its
leading space if desired.

With palette overrides enabled, the launcher answers matching child OSC 4/10/11
queries from the same configured theme. This avoids a timing dependency on
when the outer terminal applies the palette. Unconfigured queries pass
through. Theme changes should never modify Copilot's own configuration files.

## Set up the tools

From the repository root:

```pwsh
npm ci --prefix .\tools\tui-matrix
```

Captures can contain local paths, startup context, or conversation content.
Use disposable sessions and review artifacts before sharing them.

## Capture a matrix

```pwsh
.\scripts\capture-tui-matrix.ps1 -Phase Hints -ListCases
.\scripts\capture-tui-matrix.ps1 -Phase Hints
.\scripts\capture-tui-matrix.ps1 -Phase Rendering
.\scripts\capture-tui-matrix.ps1 -Phase Transport
.\scripts\capture-tui-matrix.ps1 -Phase All -Case none,truecolor,background,all
```

`-ListCases` lists cases without building or launching anything.
**Hints** runs all 16 combinations of `term`, `truecolor`, `background`, and
`github-theme`, plus forced dark/light cases. **Rendering** isolates visual
stages. **Transport** compares app-local and in-box ConPTY with visual options
disabled.

Each capture is interactive. Wait for the welcome screen to settle, then type
`/exit`. The default window is 3000 milliseconds from Copilot's first output
byte, not process creation. Override `-CaptureMilliseconds` between 250 and
30000; the dump stops at that deadline while Copilot keeps running.
Use `-OutputDirectory` for a new, non-existing destination.

The runner clears `TERM`, `COLORTERM`, `COLORFGBG`, `COPILOT_GITHUB_THEME`, and
`MAGPILOT_AGENT_TOKEN` in its child environment. Dumps force PTY hosting even
for `none`, making that case a **PTY-none** baseline, not direct-exec. Use the
separate terminal probe below to compare hosting topologies.

For rendering cases, `-ThemeFile` wins, followed by invoking-shell
`MAGPILOT_TERM_THEME_FILE`/`MAGPILOT_TERM_THEME`, then installed
`magpilot.env`. Keep Copilot version, `/theme`, terminal emulator, dimensions,
working directory, and capture duration fixed when comparing cases.

From Git Bash, invoke the PowerShell runner explicitly and pass a
comma-separated case list as one argument:

```bash
pwsh -NoProfile -File ./scripts/capture-tui-matrix.ps1 \
  -Phase All -Case 'none,truecolor,background,all'
```

Keep Git Bash and PowerShell experiments separate. The manifest records shell
markers such as `MSYSTEM`, `SHELL`, `TERM_PROGRAM`, `WT_SESSION`, and .NET's
redirection view; equal option lists do not imply equal outer terminals.

### Capture outputs

By default, outputs are under `tools\tui-matrix\captures\<timestamp>`.

| File | Meaning |
|---|---|
| `manifest.json` | Options, dimensions, child hints, resolved theme, active transformations |
| `raw.ansi` | Copilot output before Magpilot rewrites |
| `post.ansi` | Output after rewrites |
| `raw-screen.txt`, `raw-screen.json` | Normalized pre-rewrite terminal state |
| `screen.txt`, `screen.json` | Normalized post-rewrite text and styled cells |
| `diff-raw-vs-post.json` | Rows changed by Magpilot in this case |
| `diff-vs-none.json` | Rows differing from the `none` case |
| `summary.csv`, `screens-summary.csv` | Capture-root byte and visual summaries |

Compare raw screens across cases to see Copilot's response to hints; compare
raw versus post within one case to isolate rewrites. Palette application may
change the outer terminal without changing output bytes, so consult the
manifest and theme lab as well as hashes.

The Transport cases are `none` (app-local `conpty.dll` and `OpenConsole.exe`)
and `system-none` (Windows' in-box pseudoconsole). The supported default is
app-local. In-box behavior is a diagnostic control, not a feature-equivalent
replacement.

### Manual capture settings

For a focused launcher experiment outside the runner:

| Variable | Purpose |
|---|---|
| `MAGPILOT_TERM_DUMP` | Raw ANSI capture path |
| `MAGPILOT_TERM_DUMP_POST` | Post-rewrite ANSI capture path |
| `MAGPILOT_TERM_DUMP_MS` | Positive duration from first output; unset captures until exit |
| `MAGPILOT_TERM_MANIFEST` | Resolved options, hints, theme, terminal, and transformation metadata |

Dump settings can force a PTY for an otherwise agentless `none` run. Do not
interpret a captured baseline as proof that no PTY was involved.

## Offline theme lab

```pwsh
npm run editor --prefix .\tools\tui-matrix
```

Open `http://127.0.0.1:5178`. The editor discovers captured cases, replays
`raw.ansi` through xterm.js, and provides foreground/background, 16 palette
slots, thinking/input-band/legacy controls, JSON import/export, cell inspection,
and selector summaries. The selected capture and theme persist in browser
local storage.

It is intentionally offline: it never owns a live PTY or sends input to
Copilot. Theme changes rerender recorded bytes, so there is no competing input,
resize, clipboard, query-response, or session-ownership implementation.

The preview is selector-accurate, not pixel-identical to Windows Terminal.
Font rasterization, faint blending, contrast, and fixed truecolors can differ.
For palette-derived RGB output, the editor uses the recorded manifest to map
the captured values back to the corresponding edited palette slots.

Renderer logic tests:

```pwsh
npm test --prefix .\tools\tui-matrix
```

## Direct versus nested terminal probe

Run in the terminal whose behavior you are investigating:

```pwsh
.\scripts\probe-terminal-paths.ps1
```

For Git Bash:

```bash
pwsh -NoProfile -File ./scripts/probe-terminal-paths.ps1
```

The same Node probe runs directly, through app-local ConPTY, and through in-box
ConPTY. It records TTY flags, dimensions, color depth, console modes and code
pages, palette/foreground/background replies, device attributes, window size,
keyboard protocol, synchronized-output, and terminal-status replies.

Results appear under `tools\tui-matrix\captures\terminal-probe-*`.
`comparison.json` contains complete observations and a direct-versus-nested
summary. Rerun it after PTY/runtime changes rather than assuming observations
from one terminal setup apply everywhere.
