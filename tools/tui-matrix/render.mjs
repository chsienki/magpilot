import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import xterm from '@xterm/headless';

const { Terminal } = xterm;

const captureRoot = resolve(process.argv[2] ?? '');
if (!process.argv[2] || !existsSync(captureRoot)) {
  console.error('Usage: node render.mjs <capture-directory>');
  process.exit(2);
}

const caseDirectories = readdirSync(captureRoot, { withFileTypes: true })
  .filter(entry => entry.isDirectory() && existsSync(join(captureRoot, entry.name, 'manifest.json')))
  .map(entry => join(captureRoot, entry.name))
  .sort((left, right) => left.localeCompare(right));

if (caseDirectories.length === 0) {
  console.error(`No capture cases found under ${captureRoot}`);
  process.exit(3);
}

function writeTerminal(terminal, bytes) {
  return new Promise(resolveWrite => terminal.write(bytes, resolveWrite));
}

function paletteOverrides(manifest) {
  const entries = manifest.Palette ?? manifest.palette ?? [];
  return new Map(entries.map(entry => [
    entry.Index ?? entry.index,
    (entry.Color ?? entry.color).toLowerCase()
  ]));
}

function color(cell, foreground, manifest, palette) {
  const isDefault = foreground ? cell.isFgDefault() : cell.isBgDefault();
  if (isDefault) {
    const configured = foreground
      ? (manifest.Foreground ?? manifest.foreground)
      : (manifest.BackgroundColor ?? manifest.backgroundColor);
    if ((manifest.PaletteApplied ?? manifest.paletteApplied) && configured) {
      return configured.toLowerCase();
    }
    return 'default';
  }

  const isRgb = foreground ? cell.isFgRGB() : cell.isBgRGB();
  const value = foreground ? cell.getFgColor() : cell.getBgColor();
  if (isRgb) {
    return `#${value.toString(16).padStart(6, '0')}`;
  }

  const configured = palette.get(value);
  return (manifest.PaletteApplied ?? manifest.paletteApplied) && configured
    ? configured
    : `palette:${value}`;
}

function cellStyle(cell, manifest, palette) {
  return {
    fg: color(cell, true, manifest, palette),
    bg: color(cell, false, manifest, palette),
    bold: Boolean(cell.isBold()),
    dim: Boolean(cell.isDim()),
    italic: Boolean(cell.isItalic()),
    underline: Boolean(cell.isUnderline()),
    blink: Boolean(cell.isBlink()),
    inverse: Boolean(cell.isInverse()),
    invisible: Boolean(cell.isInvisible()),
    strikethrough: Boolean(cell.isStrikethrough()),
    overline: Boolean(cell.isOverline())
  };
}

function sameStyle(left, right) {
  return left.fg === right.fg &&
    left.bg === right.bg &&
    left.bold === right.bold &&
    left.dim === right.dim &&
    left.italic === right.italic &&
    left.underline === right.underline &&
    left.blink === right.blink &&
    left.inverse === right.inverse &&
    left.invisible === right.invisible &&
    left.strikethrough === right.strikethrough &&
    left.overline === right.overline;
}

function snapshot(terminal, manifest) {
  const buffer = terminal.buffer.active;
  const lines = [];
  const colors = new Set();
  const palette = paletteOverrides(manifest);

  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.getLine(buffer.viewportY + row);
    if (!line) {
      lines.push({ row, text: '', styles: [] });
      continue;
    }

    const text = line.translateToString(false, 0, terminal.cols);
    const styles = [];
    let current;

    for (let column = 0; column < terminal.cols; column++) {
      const cell = line.getCell(column);
      if (!cell) {
        continue;
      }

      const style = cellStyle(cell, manifest, palette);
      colors.add(style.fg);
      colors.add(style.bg);

      if (current && sameStyle(current.style, style)) {
        current.length++;
      }
      else {
        current = { start: column, length: 1, style };
        styles.push(current);
      }
    }

    lines.push({ row, text, styles });
  }

  return {
    case: basename(manifest.caseDirectory),
    columns: terminal.cols,
    rows: terminal.rows,
    bufferType: buffer.type,
    cursor: {
      column: buffer.cursorX,
      row: buffer.cursorY
    },
    colors: [...colors].sort(),
    lines
  };
}

function stableHash(value) {
  return createHash('sha256').update(JSON.stringify(value)).digest('hex');
}

function normalizedLine(screen, row) {
  const line = screen.lines[row] ?? { row, text: '', styles: [] };
  const text = screen.lines[row]?.text ?? '';
  if (/^\s*[●○◉◎]\s+Tip:/u.test(text)) {
    return { ...line, text: '<dynamic-tip>', styles: [] };
  }
  if (row > 0 &&
      /^\s*└\s/u.test(text) &&
      /^\s*[●○◉◎]\s+Tip:/u.test(screen.lines[row - 1]?.text ?? '')) {
    return { ...line, text: '<dynamic-tip-detail>', styles: [] };
  }
  if (/^\s*[●○◉◎]\s+Loading:/u.test(text)) {
    return { ...line, text: '<dynamic-loading-status>', styles: [] };
  }
  return line;
}

function renderText(screen) {
  return screen.lines
    .map(line => line.text.trimEnd())
    .join('\n')
    .replace(/\s+$/u, '') + '\n';
}

function compareScreens(baseline, candidate) {
  const changedRows = [];
  const rowCount = Math.max(baseline.lines.length, candidate.lines.length);
  for (let row = 0; row < rowCount; row++) {
    const left = baseline.lines[row] ?? { row, text: '', styles: [] };
    const right = candidate.lines[row] ?? { row, text: '', styles: [] };
    const normalizedLeft = normalizedLine(baseline, row);
    const normalizedRight = normalizedLine(candidate, row);
    if (normalizedLeft.text !== normalizedRight.text ||
        JSON.stringify(normalizedLeft.styles) !== JSON.stringify(normalizedRight.styles)) {
      changedRows.push({
        row,
        baselineText: left.text,
        candidateText: right.text,
        baselineStyles: left.styles,
        candidateStyles: right.styles
      });
    }
  }
  return changedRows;
}

async function renderCapture(capturePath, columns, rows, manifest) {
  if (!existsSync(capturePath)) {
    throw new Error(`Missing capture: ${capturePath}`);
  }

  const terminal = new Terminal({
    cols: columns,
    rows,
    allowProposedApi: true,
    scrollback: 0
  });
  await writeTerminal(terminal, readFileSync(capturePath));
  const screen = snapshot(terminal, manifest);
  terminal.dispose();
  return screen;
}

function visualHash(screen) {
  const { case: _, lines, ...rest } = screen;
  const visualState = {
    ...rest,
    lines: lines.map((_, row) => normalizedLine(screen, row))
  };
  return stableHash(visualState);
}

const rendered = [];
for (const caseDirectory of caseDirectories) {
  const manifestPath = join(caseDirectory, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.caseDirectory = caseDirectory;

  const columns = manifest.Columns ?? manifest.columns;
  const rows = manifest.Rows ?? manifest.rows;
  if (!Number.isInteger(columns) || !Number.isInteger(rows)) {
    throw new Error(`Manifest ${manifestPath} does not contain valid terminal dimensions.`);
  }

  const rawScreen = await renderCapture(
    join(caseDirectory, 'raw.ansi'),
    columns,
    rows,
    manifest);
  const postScreen = await renderCapture(
    join(caseDirectory, 'post.ansi'),
    columns,
    rows,
    manifest);
  const rawVsPost = compareScreens(rawScreen, postScreen);

  writeFileSync(join(caseDirectory, 'raw-screen.json'), JSON.stringify(rawScreen, null, 2));
  writeFileSync(join(caseDirectory, 'raw-screen.txt'), renderText(rawScreen));
  writeFileSync(join(caseDirectory, 'screen.json'), JSON.stringify(postScreen, null, 2));
  writeFileSync(join(caseDirectory, 'screen.txt'), renderText(postScreen));
  writeFileSync(
    join(caseDirectory, 'diff-raw-vs-post.json'),
    JSON.stringify({
      candidate: basename(caseDirectory),
      changedRowCount: rawVsPost.length,
      changedRows: rawVsPost
    }, null, 2));

  rendered.push({
    name: basename(caseDirectory),
    directory: caseDirectory,
    rawScreen,
    postScreen,
    rawHash: visualHash(rawScreen),
    postHash: visualHash(postScreen),
    changedRowsRawVsPost: rawVsPost.length
  });
}

const baseline = rendered.find(item => item.name === 'none') ?? rendered[0];
const summary = [];
for (const item of rendered) {
  const changedRows = compareScreens(baseline.postScreen, item.postScreen);
  writeFileSync(
    join(item.directory, `diff-vs-${baseline.name}.json`),
    JSON.stringify({
      baseline: baseline.name,
      candidate: item.name,
      changedRowCount: changedRows.length,
      changedRows
    }, null, 2));

  summary.push({
    case: item.name,
    rawScreenSha256: item.rawHash,
    postScreenSha256: item.postHash,
    changedRowsRawVsPost: item.changedRowsRawVsPost,
    changedRowsVsBaseline: changedRows.length,
    colors: item.postScreen.colors.join(' ')
  });
}

const csvEscape = value => `"${String(value).replaceAll('"', '""')}"`;
const csv = [
  'Case,RawScreenSha256,PostScreenSha256,ChangedRowsRawVsPost,ChangedRowsVsBaseline,Colors',
  ...summary.map(row => [
    row.case,
    row.rawScreenSha256,
    row.postScreenSha256,
    row.changedRowsRawVsPost,
    row.changedRowsVsBaseline,
    row.colors
  ].map(csvEscape).join(','))
].join('\n') + '\n';

writeFileSync(join(captureRoot, 'screens-summary.csv'), csv);
console.log(`Rendered ${rendered.length} capture(s); baseline is '${baseline.name}'.`);
