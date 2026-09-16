import { createReadStream, existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, extname, join, normalize, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const captureRoot = join(root, 'captures');
const port = Number.parseInt(process.env.MAGPILOT_THEME_LAB_PORT ?? '5178', 10);

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.mjs', 'text/javascript; charset=utf-8'],
  ['.ansi', 'application/octet-stream']
]);

function captureList() {
  if (!existsSync(captureRoot)) return [];

  const results = [];
  const visit = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const child = join(directory, entry.name);
      const manifest = join(child, 'manifest.json');
      const raw = join(child, 'raw.ansi');
      if (existsSync(manifest) && existsSync(raw)) {
        const id = relative(captureRoot, child).split(sep).join('/');
        const manifestData = JSON.parse(readFileSync(manifest, 'utf8'));
        const palette = manifestData.Palette ?? manifestData.palette ?? [];
        results.push({
          id,
          label: id,
          hasTheme: palette.length > 0,
          modifiedUtc: statSync(raw).mtime.toISOString(),
          manifestUrl: `/captures/${id}/manifest.json`,
          rawUrl: `/captures/${id}/raw.ansi`
        });
      }
      visit(child);
    }
  };
  visit(captureRoot);
  return results.sort((left, right) =>
    Number(right.hasTheme) - Number(left.hasTheme) ||
    right.modifiedUtc.localeCompare(left.modifiedUtc));
}

function staticPath(urlPath) {
  const requested = urlPath === '/' ? '/editor.html' : urlPath;
  const decoded = decodeURIComponent(requested);
  const path = resolve(root, `.${normalize(decoded)}`);
  return path === root || path.startsWith(`${root}${sep}`) ? path : null;
}

const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://${request.headers.host ?? '127.0.0.1'}`);
  if (url.pathname === '/api/captures') {
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify(captureList()));
    return;
  }

  const path = staticPath(url.pathname);
  if (!path || !existsSync(path) || !statSync(path).isFile()) {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    response.end('Not found');
    return;
  }

  response.writeHead(200, {
    'Content-Type': contentTypes.get(extname(path)) ?? 'application/octet-stream',
    'Cache-Control': 'no-store'
  });
  createReadStream(path).pipe(response);
});

server.on('error', error => {
  console.error(`Theme lab server failed: ${error.message}`);
  process.exitCode = 1;
});

server.listen(port, '127.0.0.1', () => {
  console.log(`Magpilot Theme Lab: http://127.0.0.1:${port}`);
});
