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
const canonicalUrls = new Set();
const titles = new Map();
for (const file of files.filter(file => file.endsWith('.html'))) {
  const html = await readFile(join(output, file), 'utf8');
  if (file !== 'index.html' && !/^(?:404(?:\/index)?|_not-found\/index)\.html$/.test(file)) {
    const canonical = `https://docs.orisumi.dev/${file.replace(/index\.html$/, '')}`;
    assert.ok(html.includes(`<link rel="canonical" href="${canonical}"`), `${file}: missing canonical ${canonical}`);
    canonicalUrls.add(canonical);
    const title = html.match(/<title>(.*?)<\/title>/)?.[1];
    assert.ok(title, `${file}: missing title`);
    assert.ok(!titles.has(title), `${file}: title duplicates ${titles.get(title)}: ${title}`);
    titles.set(title, file);
    assert.match(html, /<link rel="icon"[^>]*href="\/favicon-windows\.png"/, `${file}: missing default mascot favicon`);
  }
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
const sitemap = await readFile(join(output, 'sitemap.xml'), 'utf8');
const sitemapUrls = [...sitemap.matchAll(/<loc>(.*?)<\/loc>/g)].map(match => match[1]);
assert.equal(sitemapUrls.length, new Set(sitemapUrls).size, 'Sitemap contains duplicate URLs');
assert.deepEqual(new Set(sitemapUrls), canonicalUrls, 'Sitemap must contain every canonical document URL exactly once');
const robots = await readFile(join(output, 'robots.txt'), 'utf8');
assert.match(robots, /User-Agent: \*\nAllow: \//);
assert.match(robots, /Sitemap: https:\/\/docs\.orisumi\.dev\/sitemap\.xml/);
for (const platform of ['macos', 'windows']) await access(join(output, `favicon-${platform}.png`));
console.log(`Checked static export (${files.filter(file => file.endsWith('.html')).length} HTML files).`);
