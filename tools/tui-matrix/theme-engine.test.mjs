import assert from 'node:assert/strict';
import test from 'node:test';
import {
  analyzeSemanticSelectors,
  applySemanticRewrites,
  normalizeTheme,
  themeFromManifest,
  toXtermTheme
} from './theme-engine.mjs';

const decode = bytes => new TextDecoder().decode(bytes);

test('maps all sixteen palette slots into xterm theme keys', () => {
  const palette = Object.fromEntries(
    Array.from({ length: 16 }, (_, index) => [String(index), `#${index.toString(16).padStart(6, '0')}`]));

  const result = toXtermTheme({ palette, foreground: '#abcdef', background: '#123456' });

  assert.equal(result.black, '#000000');
  assert.equal(result.magenta, '#000005');
  assert.equal(result.brightMagenta, '#00000d');
  assert.equal(result.brightWhite, '#00000f');
  assert.equal(result.foreground, '#abcdef');
  assert.equal(result.background, '#123456');
});

test('rewrites faint and reset using the thinking color', () => {
  const result = decode(applySemanticRewrites(
    '\x1b[2mthought\x1b[22m',
    { thinking: '#7c8a8a' }));

  assert.equal(result, '\x1b[38;2;124;138;138mthought\x1b[22;39m');
});

test('retargets both foreground and background composer surface colors', () => {
  const result = decode(applySemanticRewrites(
    '\x1b[48;2;32;32;32mfill\x1b[38;2;227;227;228medge',
    { inputBand: '#104f5b' }));

  assert.equal(
    result,
    '\x1b[48;2;16;79;91mfill\x1b[38;2;16;79;91medge');
});

test('remaps truecolors derived from the capture palette into the edited palette', () => {
  const result = decode(applySemanticRewrites(
    '\x1b[38;2;168;140;232mloading',
    { palette: { 13: '#ef8cee' } },
    { palette: { 13: '#a88ce8' } }));

  assert.equal(result, '\x1b[38;2;239;140;238mloading');
});

test('applies legacy mappings without treating truecolor mode as faint', () => {
  const result = decode(applySemanticRewrites(
    '\x1b[48;2;58;150;221mselected\x1b[38;2;58;150;221mheading',
    { thinking: '#7c8a8a', legacyDefaultColors: true }));

  assert.equal(
    result,
    '\x1b[48;2;97;214;214mselected\x1b[38;2;188;68;167mheading');
});

test('does not treat private keyboard mode sequences as SGR', () => {
  const result = decode(applySemanticRewrites(
    '\x1b[>4;2m',
    { thinking: '#7c8a8a' }));

  assert.equal(result, '\x1b[>4;2m');
});

test('counts semantic selectors that can affect a capture', () => {
  const counts = analyzeSemanticSelectors(
    '\x1b[2mfaint' +
    '\x1b[48;2;32;32;32mband' +
    '\x1b[38;2;58;150;221mlegacy' +
    '\x1b[>4;2mprivate');

  assert.deepEqual(counts, {
    thinking: 1,
    inputBand: 1,
    legacyColors: 1
  });
});

test('creates an editable theme from a capture manifest', () => {
  const theme = themeFromManifest({
    Palette: [{ Index: 5, Color: '#D75BD6' }],
    Foreground: '#C2CCC6',
    BackgroundColor: '#002B36',
    InputBand: '#104F5B',
    LegacyDefaultColors: true
  });

  assert.deepEqual(theme.palette, { 5: '#d75bd6' });
  assert.equal(theme.foreground, '#c2ccc6');
  assert.equal(theme.background, '#002b36');
  assert.equal(theme.inputBand, '#104f5b');
  assert.equal(theme.legacyDefaultColors, true);
});

test('normalization drops malformed optional colors', () => {
  const theme = normalizeTheme({
    palette: { 0: '#002b36', 1: 'invalid' },
    foreground: 'also-invalid'
  });

  assert.deepEqual(theme.palette, { 0: '#002b36' });
  assert.equal(theme.foreground, undefined);
});
