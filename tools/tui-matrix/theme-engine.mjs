const paletteKeys = [
  'black',
  'red',
  'green',
  'yellow',
  'blue',
  'magenta',
  'cyan',
  'white',
  'brightBlack',
  'brightRed',
  'brightGreen',
  'brightYellow',
  'brightBlue',
  'brightMagenta',
  'brightCyan',
  'brightWhite'
];

const surfaceColors = new Set([
  '32;32;32',
  '227;227;228'
]);

const legacyColors = new Map([
  ['48;58;150;221', '97;214;214'],
  ['48;59;120;255', '97;214;214'],
  ['48;9;105;218', '97;214;214'],
  ['38;255;255;255', '12;12;12'],
  ['38;58;150;221', '188;68;167'],
  ['38;0;55;218', '59;120;255'],
  ['38;9;105;218', '59;120;255'],
  ['38;19;161;14', '22;198;12'],
  ['38;136;23;152', '188;68;167'],
  ['38;180;0;158', '188;68;167'],
  ['38;52;56;60', '134;134;134'],
  ['38;97;100;104', '134;134;134'],
  ['38;122;124;128', '112;112;112'],
  ['38;147;149;152', '134;134;134'],
  ['38;177;186;196', '134;134;134'],
  ['38;145;152;161', '112;112;112']
]);

const decoder = new TextDecoder();
const encoder = new TextEncoder();

export function normalizeTheme(value = {}) {
  const palette = {};
  for (let index = 0; index < 16; index++) {
    const color = value.palette?.[String(index)];
    if (isColor(color)) {
      palette[String(index)] = normalizeColor(color);
    }
  }

  return {
    ...value,
    palette,
    foreground: isColor(value.foreground) ? normalizeColor(value.foreground) : undefined,
    background: isColor(value.background) ? normalizeColor(value.background) : undefined,
    thinking: isColor(value.thinking) ? normalizeColor(value.thinking) : undefined,
    inputBand: isColor(value.inputBand) ? normalizeColor(value.inputBand) : undefined,
    legacyDefaultColors: value.legacyDefaultColors === true
  };
}

export function themeFromManifest(manifest) {
  const entries = manifest.Palette ?? manifest.palette ?? [];
  const palette = Object.fromEntries(entries.map(entry => [
    String(entry.Index ?? entry.index),
    entry.Color ?? entry.color
  ]));

  return normalizeTheme({
    palette,
    foreground: manifest.Foreground ?? manifest.foreground,
    background: manifest.BackgroundColor ?? manifest.backgroundColor,
    thinking: manifest.Thinking ?? manifest.thinking,
    inputBand: manifest.InputBand ?? manifest.inputBand,
    legacyDefaultColors:
      (manifest.LegacyDefaultColors ?? manifest.legacyDefaultColors) === true
  });
}

export function toXtermTheme(themeValue) {
  const theme = normalizeTheme(themeValue);
  const result = {};

  if (theme.foreground) result.foreground = theme.foreground;
  if (theme.background) result.background = theme.background;
  for (let index = 0; index < paletteKeys.length; index++) {
    const value = theme.palette[String(index)];
    if (value) result[paletteKeys[index]] = value;
  }

  return result;
}

export function applySemanticRewrites(bytes, themeValue, sourceThemeValue) {
  const theme = normalizeTheme(themeValue);
  const colorRemap = buildColorRemap(
    normalizeTheme(sourceThemeValue),
    theme);
  const text = typeof bytes === 'string' ? bytes : decoder.decode(bytes);
  const rewritten = text.replace(
    /\x1b\[([0-9;]*)m/g,
    (sequence, parameters) => rewriteSgr(sequence, parameters, theme, colorRemap));
  return encoder.encode(rewritten);
}

export function analyzeSemanticSelectors(bytes) {
  const text = typeof bytes === 'string' ? bytes : decoder.decode(bytes);
  const counts = {
    thinking: 0,
    inputBand: 0,
    legacyColors: 0
  };

  for (const match of text.matchAll(/\x1b\[([0-9;]*)m/g)) {
    const tokens = match[1].split(';');
    for (let index = 0; index < tokens.length; index++) {
      const token = tokens[index];
      if (token === '38' || token === '48') {
        if (index + 1 >= tokens.length) continue;
        const mode = tokens[++index];
        const length = mode === '2' ? 3 : mode === '5' ? 1 : 0;
        const values = tokens.slice(index + 1, index + 1 + length);
        if (mode === '2') {
          if (surfaceColors.has(values.join(';'))) counts.inputBand++;
          if (legacyColors.has(`${token};${values.join(';')}`)) counts.legacyColors++;
        }
        index += length;
      }
      else if (token === '2') {
        counts.thinking++;
      }
    }
  }

  return counts;
}

export function selectorForCell(cell, foreground, themeValue) {
  const theme = normalizeTheme(themeValue);
  const prefix = foreground ? 'foreground' : 'background';

  if (foreground ? cell.isFgDefault() : cell.isBgDefault()) {
    return {
      kind: 'default',
      key: prefix,
      label: `default ${prefix}`,
      color: theme[prefix]
    };
  }

  if (foreground ? cell.isFgPalette() : cell.isBgPalette()) {
    const index = foreground ? cell.getFgColor() : cell.getBgColor();
    return {
      kind: 'palette',
      key: String(index),
      label: `ANSI ${index}`,
      color: theme.palette[String(index)]
    };
  }

  const numeric = foreground ? cell.getFgColor() : cell.getBgColor();
  const color = `#${numeric.toString(16).padStart(6, '0')}`;
  const semantic = semanticKeyForColor(color, theme);
  return {
    kind: semantic ? 'semantic' : 'rgb',
    key: semantic,
    label: semantic ?? `explicit RGB ${color}`,
    color
  };
}

export function semanticKeyForColor(color, themeValue) {
  const normalized = normalizeColor(color);
  const theme = normalizeTheme(themeValue);
  if (theme.thinking === normalized) return 'thinking';
  if (theme.inputBand === normalized) return 'inputBand';
  return null;
}

export function colorUsage(terminal, selector, foreground) {
  const buffer = terminal.buffer.active;
  let count = 0;

  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.getLine(buffer.viewportY + row);
    if (!line) continue;

    for (let column = 0; column < terminal.cols; column++) {
      const cell = line.getCell(column);
      if (!cell) continue;

      if (selector.kind === 'default' &&
          (foreground ? cell.isFgDefault() : cell.isBgDefault())) {
        count++;
      }
      else if (selector.kind === 'palette' &&
               (foreground ? cell.isFgPalette() : cell.isBgPalette()) &&
               String(foreground ? cell.getFgColor() : cell.getBgColor()) === selector.key) {
        count++;
      }
      else if ((selector.kind === 'semantic' || selector.kind === 'rgb') &&
               (foreground ? cell.isFgRGB() : cell.isBgRGB())) {
        const numeric = foreground ? cell.getFgColor() : cell.getBgColor();
        const color = `#${numeric.toString(16).padStart(6, '0')}`;
        if (normalizeColor(color) === normalizeColor(selector.color)) count++;
      }
    }
  }

  return count;
}

export function normalizeColor(value) {
  if (!isColor(value)) return undefined;
  const color = value.startsWith('#') ? value : `#${value}`;
  if (color.length === 4) {
    return `#${color[1]}${color[1]}${color[2]}${color[2]}${color[3]}${color[3]}`.toLowerCase();
  }
  return color.toLowerCase();
}

function isColor(value) {
  return typeof value === 'string' && /^#?[0-9a-f]{3}(?:[0-9a-f]{3})?$/i.test(value);
}

function rewriteSgr(sequence, parameters, theme, colorRemap) {
  const tokens = parameters.split(';');
  const result = [];

  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index];

    if (token === '38' || token === '48') {
      result.push(token);
      if (index + 1 >= tokens.length) continue;

      const mode = tokens[++index];
      result.push(mode);
      const length = mode === '2' ? 3 : mode === '5' ? 1 : 0;
      const values = tokens.slice(index + 1, index + 1 + length);

      if (mode === '2' && theme.inputBand && surfaceColors.has(values.join(';'))) {
        result.push(...rgbTokens(theme.inputBand));
      }
      else if (mode === '2' && colorRemap.has(values.join(';'))) {
        result.push(...colorRemap.get(values.join(';')));
      }
      else if (mode === '2' && theme.legacyDefaultColors) {
        const replacement = legacyColors.get(`${token};${values.join(';')}`);
        result.push(...(replacement?.split(';') ?? values));
      }
      else {
        result.push(...values);
      }

      index += length;
      continue;
    }

    if (token === '2' && theme.thinking) {
      result.push('38', '2', ...rgbTokens(theme.thinking));
    }
    else if (token === '22' && theme.thinking) {
      result.push('22', '39');
    }
    else {
      result.push(token);
    }
  }

  return `\x1b[${result.join(';')}m`;
}

function rgbTokens(color) {
  const value = normalizeColor(color);
  return [
    String(Number.parseInt(value.slice(1, 3), 16)),
    String(Number.parseInt(value.slice(3, 5), 16)),
    String(Number.parseInt(value.slice(5, 7), 16))
  ];
}

function buildColorRemap(source, target) {
  const result = new Map();
  if (!source) return result;

  for (let index = 0; index < 16; index++) {
    const sourceColor = source.palette[String(index)];
    const targetColor = target.palette[String(index)];
    if (sourceColor && targetColor) {
      result.set(rgbTokens(sourceColor).join(';'), rgbTokens(targetColor));
    }
  }

  for (const key of ['foreground', 'background']) {
    if (source[key] && target[key]) {
      result.set(rgbTokens(source[key]).join(';'), rgbTokens(target[key]));
    }
  }

  return result;
}
