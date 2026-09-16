import { Terminal } from '/node_modules/@xterm/xterm/lib/xterm.mjs';
import {
  analyzeSemanticSelectors,
  applySemanticRewrites,
  colorUsage,
  normalizeColor,
  normalizeTheme,
  selectorForCell,
  themeFromManifest,
  toXtermTheme
} from './theme-engine.mjs';

const paletteNames = [
  'Black',
  'Red',
  'Green',
  'Yellow',
  'Blue',
  'Magenta',
  'Cyan',
  'White',
  'Bright black',
  'Bright red',
  'Bright green',
  'Bright yellow',
  'Bright blue',
  'Bright magenta',
  'Bright cyan',
  'Bright white'
];

const fallbackPalette = [
  '#000000',
  '#cd0000',
  '#00cd00',
  '#cdcd00',
  '#0000ee',
  '#cd00cd',
  '#00cdcd',
  '#e5e5e5',
  '#7f7f7f',
  '#ff0000',
  '#00ff00',
  '#ffff00',
  '#5c5cff',
  '#ff00ff',
  '#00ffff',
  '#ffffff'
];

const captureStorageKey = 'magpilot.themeLab.capture';
const themeStorageKey = 'magpilot.themeLab.theme';

const elements = {
  baseColors: document.querySelector('#base-colors'),
  boldBright: document.querySelector('#bold-bright'),
  capture: document.querySelector('#capture'),
  captureTheme: document.querySelector('#capture-theme'),
  exportTheme: document.querySelector('#export-theme'),
  highlight: document.querySelector('#cell-highlight'),
  inspector: document.querySelector('#inspector'),
  legacyColors: document.querySelector('#legacy-colors'),
  legacyDescription: document.querySelector('#legacy-description'),
  minimumContrast: document.querySelector('#minimum-contrast'),
  palette: document.querySelector('#palette'),
  semanticColors: document.querySelector('#semantic-colors'),
  status: document.querySelector('#status'),
  terminal: document.querySelector('#terminal'),
  terminalWrap: document.querySelector('#terminal-wrap'),
  themeFile: document.querySelector('#theme-file')
};

let captureManifest;
let captureTheme = normalizeTheme();
let rawBytes;
let terminal;
let theme = normalizeTheme();
let renderGeneration = 0;
let renderTimer;
let semanticMatches = {
  thinking: 0,
  inputBand: 0,
  legacyColors: 0
};
const rendererOptions = {
  boldBright: true,
  minimumContrast: 1
};

function setStatus(message) {
  elements.status.textContent = message;
}

function scheduleRender() {
  localStorage.setItem(themeStorageKey, JSON.stringify(theme));
  clearTimeout(renderTimer);
  renderTimer = setTimeout(renderTerminal, 50);
}

function createColorControl(container, {
  id,
  title,
  subtitle,
  get,
  set,
  remove,
  fallback,
  warning,
  matches
}) {
  const row = document.createElement('div');
  row.className = 'color-row';
  row.dataset.control = id;
  if (matches === 0) row.classList.add('no-matches');

  const enabled = document.createElement('input');
  enabled.type = 'checkbox';
  enabled.checked = Boolean(get());

  const label = document.createElement('span');
  label.className = 'color-label';
  const strong = document.createElement('strong');
  strong.textContent = title;
  const small = document.createElement('small');
  small.textContent = subtitle;
  if (warning) small.classList.add('warning');
  label.append(strong, small);

  const color = document.createElement('input');
  color.type = 'color';
  color.value = get() ?? fallback;
  color.disabled = !enabled.checked;

  const text = document.createElement('input');
  text.type = 'text';
  text.value = get() ?? '';
  text.placeholder = fallback;

  const applyValue = value => {
    const normalized = normalizeColor(value);
    if (!normalized) return;
    enabled.checked = true;
    color.disabled = false;
    color.value = normalized;
    text.value = normalized;
    set(normalized);
    scheduleRender();
  };

  enabled.addEventListener('change', () => {
    color.disabled = !enabled.checked;
    if (enabled.checked) {
      applyValue(text.value || color.value || fallback);
    }
    else {
      text.value = '';
      remove();
      scheduleRender();
    }
  });
  color.addEventListener('input', () => applyValue(color.value));
  text.addEventListener('change', () => applyValue(text.value));

  row.append(enabled, label, color, text);
  container.append(row);
}

function renderControls() {
  elements.baseColors.replaceChildren();
  elements.palette.replaceChildren();
  elements.semanticColors.replaceChildren();

  for (const [key, title, fallback] of [
    ['foreground', 'Foreground', '#c0c0c0'],
    ['background', 'Background', '#000000']
  ]) {
    createColorControl(elements.baseColors, {
      id: `base-${key}`,
      title,
      subtitle: `Default ${key}`,
      get: () => theme[key],
      set: value => { theme[key] = value; },
      remove: () => { delete theme[key]; },
      fallback
    });
  }

  for (let index = 0; index < 16; index++) {
    createColorControl(elements.palette, {
      id: `palette-${index}`,
      title: `${index}: ${paletteNames[index]}`,
      subtitle: index === 5 ? 'Normal magenta' : index === 13 ? 'Bright magenta' : `ANSI slot ${index}`,
      get: () => theme.palette[String(index)],
      set: value => { theme.palette[String(index)] = value; },
      remove: () => { delete theme.palette[String(index)]; },
      fallback: fallbackPalette[index]
    });
  }

  createColorControl(elements.semanticColors, {
    id: 'semantic-thinking',
    title: 'Thinking / faint',
    subtitle:
      `${semanticMatches.thinking} matching faint selector(s). ` +
      'Affects borders and separators too.',
    get: () => theme.thinking,
    set: value => { theme.thinking = value; },
    remove: () => { delete theme.thinking; },
    fallback: '#7c8a8a',
    warning: true,
    matches: semanticMatches.thinking
  });
  createColorControl(elements.semanticColors, {
    id: 'semantic-inputBand',
    title: 'Input band',
    subtitle: `${semanticMatches.inputBand} matching composer surface selector(s).`,
    get: () => theme.inputBand,
    set: value => { theme.inputBand = value; },
    remove: () => { delete theme.inputBand; },
    fallback: '#104f5b',
    matches: semanticMatches.inputBand
  });

  elements.legacyColors.checked = theme.legacyDefaultColors;
  elements.legacyDescription.textContent =
    `${semanticMatches.legacyColors} matching known truecolor selector(s).`;
}

async function renderTerminal() {
  if (!rawBytes || !captureManifest) return;

  const generation = ++renderGeneration;
  elements.highlight.hidden = true;
  terminal?.dispose();
  elements.terminal.replaceChildren();

  const columns = captureManifest.Columns ?? captureManifest.columns ?? 120;
  const rows = captureManifest.Rows ?? captureManifest.rows ?? 40;
  terminal = new Terminal({
    cols: columns,
    rows,
    allowProposedApi: true,
    convertEol: false,
    cursorBlink: false,
    disableStdin: true,
    drawBoldTextInBrightColors: rendererOptions.boldBright,
    fontFamily: '"Inconsolata Nerd Font", Consolas, monospace',
    fontSize: 12,
    minimumContrastRatio: rendererOptions.minimumContrast,
    scrollback: 0,
    theme: toXtermTheme(theme)
  });
  terminal.open(elements.terminal);

  const output = applySemanticRewrites(rawBytes, theme, captureTheme);
  terminal.write(output, () => {
    if (generation !== renderGeneration) return;
    const statistics = selectorStatistics(rawBytes);
    setStatus(
      `${elements.capture.selectedOptions[0]?.textContent ?? 'Capture'} | ` +
      `${columns}x${rows} | ${output.byteLength.toLocaleString()} bytes | ` +
      `${statistics.truecolor} truecolor, ${statistics.palette} ANSI, ` +
      `${statistics.faint} faint selectors`);
  });
}

function selectorStatistics(bytes) {
  const text = new TextDecoder().decode(bytes);
  return {
    truecolor: (text.match(/\x1b\[(?:38|48);2;/g) ?? []).length,
    palette: (text.match(/\x1b\[(?:(?:38|48);5;|(?:3[0-7]|4[0-7]|9[0-7]|10[0-7])m)/g) ?? []).length,
    faint: (text.match(/\x1b\[(?:[0-9;]*;)?2m/g) ?? []).length
  };
}

async function loadCapture(value) {
  const option = elements.capture.querySelector(`option[value="${CSS.escape(value)}"]`);
  if (!option) return;

  setStatus(`Loading ${option.textContent}...`);
  const [manifestResponse, rawResponse] = await Promise.all([
    fetch(option.dataset.manifest),
    fetch(option.dataset.raw)
  ]);
  if (!manifestResponse.ok || !rawResponse.ok) {
    throw new Error(`Failed to load capture ${option.textContent}`);
  }

  captureManifest = await manifestResponse.json();
  rawBytes = new Uint8Array(await rawResponse.arrayBuffer());
  captureTheme = themeFromManifest(captureManifest);
  semanticMatches = analyzeSemanticSelectors(rawBytes);
  localStorage.setItem(captureStorageKey, value);
  if (Object.keys(theme.palette).length === 0 && Object.keys(captureTheme.palette).length > 0) {
    theme = normalizeTheme(captureTheme);
  }
  renderControls();
  await renderTerminal();
}

function useCaptureTheme() {
  theme = normalizeTheme(captureTheme);
  renderControls();
  scheduleRender();
}

async function loadThemeFile(file) {
  const parsed = JSON.parse(await file.text());
  theme = normalizeTheme(parsed);
  renderControls();
  scheduleRender();
  setStatus(`Loaded theme ${file.name}`);
}

function exportTheme() {
  const output = {
    ...theme,
    palette: Object.fromEntries(
      Object.entries(theme.palette).sort(([left], [right]) => Number(left) - Number(right)))
  };
  for (const key of ['foreground', 'background', 'thinking', 'inputBand']) {
    if (!output[key]) delete output[key];
  }
  if (!output.legacyDefaultColors) delete output.legacyDefaultColors;

  const blob = new Blob([`${JSON.stringify(output, null, 2)}\n`], {
    type: 'application/json'
  });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = 'magpilot-theme.json';
  link.click();
  URL.revokeObjectURL(link.href);
}

function selectControl(selector) {
  document.querySelectorAll('.color-row.selected')
    .forEach(row => row.classList.remove('selected'));

  const id = selector.kind === 'palette'
    ? `palette-${selector.key}`
    : selector.kind === 'default'
      ? `base-${selector.key}`
      : selector.kind === 'semantic'
        ? `semantic-${selector.key}`
        : null;
  if (!id) return;

  const control = document.querySelector(`[data-control="${id}"]`);
  control?.classList.add('selected');
  control?.scrollIntoView({ block: 'nearest' });
}

function inspectorBlock(title, selector, usage) {
  const block = document.createElement('div');
  block.className = 'inspection-block';
  const heading = document.createElement('strong');
  heading.textContent = title;
  const details = document.createElement('div');
  details.textContent =
    `${selector.label}` +
    `${selector.color ? ` (${selector.color})` : ''} | ${usage} visible cells`;
  block.append(heading, details);
  return block;
}

function inspectCell(event) {
  if (!terminal) return;

  const screen = elements.terminal.querySelector('.xterm-screen');
  if (!screen) return;
  const rect = screen.getBoundingClientRect();
  if (event.clientX < rect.left || event.clientX >= rect.right ||
      event.clientY < rect.top || event.clientY >= rect.bottom) {
    return;
  }

  const column = Math.min(
    terminal.cols - 1,
    Math.floor((event.clientX - rect.left) / (rect.width / terminal.cols)));
  const row = Math.min(
    terminal.rows - 1,
    Math.floor((event.clientY - rect.top) / (rect.height / terminal.rows)));
  const line = terminal.buffer.active.getLine(terminal.buffer.active.viewportY + row);
  const cell = line?.getCell(column);
  if (!cell) return;

  const foreground = selectorForCell(cell, true, theme);
  const background = selectorForCell(cell, false, theme);
  const foregroundUsage = colorUsage(terminal, foreground, true);
  const backgroundUsage = colorUsage(terminal, background, false);

  elements.inspector.classList.remove('empty');
  elements.inspector.replaceChildren();
  const location = document.createElement('div');
  location.textContent =
    `row ${row}, column ${column}, character ${JSON.stringify(cell.getChars() || ' ')}`;
  const attributes = document.createElement('div');
  attributes.textContent = [
    cell.isBold() ? 'bold' : null,
    cell.isDim() ? 'faint/dim' : null,
    cell.isItalic() ? 'italic' : null,
    cell.isUnderline() ? 'underline' : null,
    cell.isInverse() ? 'inverse' : null
  ].filter(Boolean).join(', ') || 'no text attributes';
  elements.inspector.append(
    location,
    attributes,
    inspectorBlock('Foreground', foreground, foregroundUsage),
    inspectorBlock('Background', background, backgroundUsage));

  const preferred = foreground.kind !== 'default' ? foreground : background;
  selectControl(preferred);

  const wrapRect = elements.terminalWrap.getBoundingClientRect();
  const cellWidth = rect.width / terminal.cols;
  const cellHeight = rect.height / terminal.rows;
  elements.highlight.hidden = false;
  elements.highlight.style.left =
    `${rect.left - wrapRect.left + elements.terminalWrap.scrollLeft + column * cellWidth}px`;
  elements.highlight.style.top =
    `${rect.top - wrapRect.top + elements.terminalWrap.scrollTop + row * cellHeight}px`;
  elements.highlight.style.width = `${cellWidth}px`;
  elements.highlight.style.height = `${cellHeight}px`;
}

async function initialize() {
  const storedTheme = localStorage.getItem(themeStorageKey);
  if (storedTheme) {
    try { theme = normalizeTheme(JSON.parse(storedTheme)); }
    catch { localStorage.removeItem(themeStorageKey); }
  }
  renderControls();
  elements.legacyColors.addEventListener('change', () => {
    theme.legacyDefaultColors = elements.legacyColors.checked;
    scheduleRender();
  });
  elements.boldBright.addEventListener('change', () => {
    rendererOptions.boldBright = elements.boldBright.checked;
    scheduleRender();
  });
  elements.minimumContrast.addEventListener('change', () => {
    rendererOptions.minimumContrast = Number(elements.minimumContrast.value);
    scheduleRender();
  });
  elements.capture.addEventListener('change', () => loadCapture(elements.capture.value));
  elements.captureTheme.addEventListener('click', useCaptureTheme);
  elements.exportTheme.addEventListener('click', exportTheme);
  elements.themeFile.addEventListener('change', () => {
    const [file] = elements.themeFile.files;
    if (file) loadThemeFile(file).catch(showError);
  });
  elements.terminalWrap.addEventListener('click', inspectCell);

  const response = await fetch('/api/captures');
  if (!response.ok) throw new Error('Failed to list captures');
  const captures = await response.json();
  for (const capture of captures) {
    const option = document.createElement('option');
    option.value = capture.id;
    option.textContent = capture.label;
    option.dataset.manifest = capture.manifestUrl;
    option.dataset.raw = capture.rawUrl;
    elements.capture.append(option);
  }

  if (captures.length === 0) {
    setStatus('No captures found. Run capture-tui-matrix.ps1 first.');
    return;
  }
  const storedCapture = localStorage.getItem(captureStorageKey);
  const initialCapture = captures.some(capture => capture.id === storedCapture)
    ? storedCapture
    : captures[0].id;
  elements.capture.value = initialCapture;
  await loadCapture(initialCapture);
}

function showError(error) {
  console.error(error);
  setStatus(error instanceof Error ? error.message : String(error));
}

initialize().catch(showError);
