import assert from 'node:assert/strict';
import { access, readFile, readdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve('out');
for (const path of ['index.html', '404.html', 'start/index.html', 'core/index.html',
  'testing/reports/integration/index.html', 'core/model/version.md',
  'llms.txt', 'llms-full.txt', 'og/start/image.png', '.nojekyll']) {
  await access(join(output, path));
}

const index = JSON.parse(await readFile(join(output, 'api/search'), 'utf8'));
assert.ok(index && typeof index === 'object', 'Search must export a JSON index');
const markdown = await readFile(join(output, 'core/model/version.md'), 'utf8');
assert.match(markdown, /^# Version/m);

// Check the real output: every root-relative asset, page and Markdown link must exist.
const missing = new Set();
const files = await readdir(output, { recursive: true });
for (const file of files.filter(file => file.endsWith('.html'))) {
  const html = await readFile(join(output, file), 'utf8');
  for (const match of html.matchAll(/(?:href|src)="(\/(?!\/)[^"?#]*)/g)) {
    const path = decodeURIComponent(match[1]);
    const target = join(output, path);
    try { await access(target); }
    catch {
      try { await access(join(target, 'index.html')); }
      catch { missing.add(`${file}: ${path}`); }
    }
  }
}
assert.deepEqual([...missing], [], 'Static export contains missing links');
console.log(`Checked static export (${files.filter(file => file.endsWith('.html')).length} HTML files).`);
