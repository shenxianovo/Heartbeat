import { copyFile, mkdir, readdir, writeFile } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';

const output = resolve('out');
const markdown = join(output, 'llms.mdx');

// GitHub Pages cannot rewrite /page.md to the longer Markdown export route; the dev server does it in next.config.mjs.
for (const entry of await readdir(markdown, { recursive: true, withFileTypes: true })) {
  if (!entry.isFile() || entry.name !== 'content.md') continue;
  const source = join(entry.parentPath, entry.name);
  const target = join(output, dirname(relative(markdown, source)) + '.md');
  await mkdir(dirname(target), { recursive: true });
  await copyFile(source, target);
}

await writeFile(join(output, '.nojekyll'), '');
await writeFile(join(output, 'index.html'), `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>Orisumi</title>
<meta http-equiv="refresh" content="0;url=/start/">
<link rel="canonical" href="https://docs.orisumi.dev/start/">
<link rel="icon" href="/favicon-windows.png" type="image/png" sizes="48x48"></head>
<body><a href="/start/">Orisumi</a></body></html>\n`);
