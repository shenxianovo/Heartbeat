import assert from 'node:assert/strict';
import { test } from 'node:test';
import { loader } from 'fumadocs-core/source';
import { getCanonicalUrl, getPageTitle } from '../lib/metadata.ts';

test('metadata titles distinguish section indexes and nested pages without changing source titles', () => {
  const source = loader({ files: [
    { type: 'meta', path: 'core/meta.json', data: { title: 'Core', root: true } },
    { type: 'page', path: 'core/index.mdx', data: { title: 'Overview' } },
    { type: 'meta', path: 'observers/meta.json', data: { title: 'Observers', root: true } },
    { type: 'meta', path: 'observers/submission/meta.json', data: { title: 'Submission' } },
    { type: 'page', path: 'observers/index.mdx', data: { title: 'Overview' } },
    { type: 'page', path: 'observers/submission/index.mdx', data: { title: 'Submission' } },
    { type: 'page', path: 'testing/api/submission.mdx', data: { title: 'Submission' } },
    { type: 'meta', path: 'testing/meta.json', data: { title: 'Testing', root: true } },
  ] }, { baseUrl: '/' });
  const titles = source.getPages().map((page) => getPageTitle(page, source.getPageTree()));
  assert.equal(new Set(titles).size, titles.length);
  assert.ok(titles.includes('Overview · Core | Orisumi'));
  assert.ok(titles.includes('Submission · Observers | Orisumi'));
  assert.equal(source.getPage(['core'])?.data.title, 'Overview');
  assert.equal(getPageTitle({ url: '/start', data: { title: 'Introduction' } }, source.getPageTree()),
    'Orisumi 文档：保存一个人经历了什么');
});

test('canonical URLs consistently use the public origin and a trailing slash', () => {
  assert.equal(getCanonicalUrl('/core/model/version'), 'https://docs.orisumi.dev/core/model/version/');
  assert.equal(getCanonicalUrl('/core/'), 'https://docs.orisumi.dev/core/');
});
