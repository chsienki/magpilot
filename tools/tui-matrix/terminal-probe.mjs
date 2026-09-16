import { readFileSync, writeFileSync } from 'node:fs';
import { performance } from 'node:perf_hooks';
import { spawnSync } from 'node:child_process';

const outputPath = process.env.MAGPILOT_TERMINAL_PROBE_OUTPUT;
if (!outputPath) {
  console.error('MAGPILOT_TERMINAL_PROBE_OUTPUT is required.');
  process.exit(2);
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function describeBytes(buffer) {
  let result = '';
  for (const value of buffer) {
    if (value === 0x1b) result += '<ESC>';
    else if (value === 0x07) result += '<BEL>';
    else if (value === 0x0d) result += '<CR>';
    else if (value === 0x0a) result += '<LF>';
    else if (value >= 0x20 && value <= 0x7e) result += String.fromCharCode(value);
    else result += `<${value.toString(16).padStart(2, '0')}>`;
  }
  return result;
}

function safeCall(callback) {
  try {
    return callback();
  }
  catch (error) {
    return { error: String(error) };
  }
}

const groups = [
  {
    name: 'palette',
    query: [
      ...Array.from({ length: 16 }, (_, index) => `\x1b]4;${index};?\x1b\\`),
      '\x1b]10;?\x1b\\',
      '\x1b]11;?\x1b\\'
    ].join(''),
    waitMilliseconds: 600
  },
  {
    name: 'device',
    query: '\x1b[c\x1b[>c\x1b[14t\x1b[18t',
    waitMilliseconds: 400
  },
  {
    name: 'modes',
    query: '\x1b[?12$p\x1b[?1007$p\x1b[?2026$p\x1b[?u\x1b[?996n',
    waitMilliseconds: 600
  }
];

const chunks = [];
let activeGroup = null;
const startedAt = performance.now();
process.stdin.on('data', chunk => {
  chunks.push({
    group: activeGroup,
    atMilliseconds: Math.round((performance.now() - startedAt) * 1000) / 1000,
    hex: chunk.toString('hex'),
    display: describeBytes(chunk)
  });
});

const couldSetRawMode = typeof process.stdin.setRawMode === 'function';
let rawModeEnabled = false;
if (couldSetRawMode) {
  try {
    process.stdin.setRawMode(true);
    rawModeEnabled = true;
  }
  catch {
    rawModeEnabled = false;
  }
}
process.stdin.resume();

let consoleMode = null;
const consoleModeHelper = process.env.MAGPILOT_CONSOLE_MODE_HELPER;
if (consoleModeHelper) {
  const consoleModePath = `${outputPath}.console-mode.json`;
  const helper = spawnSync(
    consoleModeHelper,
    [`--magpilot-console-mode-snapshot=${consoleModePath}`],
    { stdio: 'inherit' });
  if (helper.status === 0) {
    consoleMode = JSON.parse(readFileSync(consoleModePath, 'utf8'));
  }
  else {
    consoleMode = {
      error: `console-mode helper exited with ${helper.status}`,
      signal: helper.signal
    };
  }
}

const replies = [];
for (const group of groups) {
  activeGroup = group.name;
  const firstChunk = chunks.length;
  process.stdout.write(group.query);
  await delay(group.waitMilliseconds);

  const groupChunks = chunks.slice(firstChunk);
  const combined = Buffer.concat(groupChunks.map(chunk => Buffer.from(chunk.hex, 'hex')));
  replies.push({
    name: group.name,
    queryHex: Buffer.from(group.query, 'latin1').toString('hex'),
    waitMilliseconds: group.waitMilliseconds,
    responseBytes: combined.length,
    responseHex: combined.toString('hex'),
    responseDisplay: describeBytes(combined),
    chunks: groupChunks
  });
}
activeGroup = null;

if (rawModeEnabled) {
  try {
    process.stdin.setRawMode(false);
  }
  catch {
    // The process is exiting; the terminal owner restores its mode.
  }
}
process.stdin.pause();

const result = {
  capturedAtUtc: new Date().toISOString(),
  process: {
    platform: process.platform,
    node: process.version,
    stdinIsTTY: Boolean(process.stdin.isTTY),
    stdoutIsTTY: Boolean(process.stdout.isTTY),
    stderrIsTTY: Boolean(process.stderr.isTTY),
    stdinRawModeAvailable: couldSetRawMode,
    stdinRawModeEnabled: rawModeEnabled,
    columns: process.stdout.columns ?? null,
    rows: process.stdout.rows ?? null,
    colorDepth: typeof process.stdout.getColorDepth === 'function'
      ? safeCall(() => process.stdout.getColorDepth())
      : null,
    hasColors16: typeof process.stdout.hasColors === 'function'
      ? safeCall(() => process.stdout.hasColors(16))
      : null,
    hasColors256: typeof process.stdout.hasColors === 'function'
      ? safeCall(() => process.stdout.hasColors(256))
      : null,
    hasColorsTrueColor: typeof process.stdout.hasColors === 'function'
      ? safeCall(() => process.stdout.hasColors(1 << 24))
      : null,
    stdinHandleType: process.stdin._handle?.constructor?.name ?? null,
    stdoutHandleType: process.stdout._handle?.constructor?.name ?? null,
    stderrHandleType: process.stderr._handle?.constructor?.name ?? null
  },
  environment: {
    TERM: process.env.TERM ?? null,
    COLORTERM: process.env.COLORTERM ?? null,
    COLORFGBG: process.env.COLORFGBG ?? null,
    COPILOT_GITHUB_THEME: process.env.COPILOT_GITHUB_THEME ?? null,
    MSYSTEM: process.env.MSYSTEM ?? null,
    SHELL: process.env.SHELL ?? null,
    TERM_PROGRAM: process.env.TERM_PROGRAM ?? null,
    WT_SESSION: process.env.WT_SESSION ?? null
  },
  consoleMode,
  replies
};

writeFileSync(outputPath, JSON.stringify(result, null, 2));
