import assert from 'node:assert/strict';
import { test } from 'node:test';
import { linkToMarkdown, markdownUrl } from '../lib/markdown-links.ts';

test('page URLs become Markdown addresses with their anchors', () => {
  assert.equal(markdownUrl('/core/model/version'), '/core/model/version.md');
  assert.equal(markdownUrl('/core/model/version#members'), '/core/model/version.md#members');
});

test('links to pages point at Markdown, other links stay as written', () => {
  const pages: Record<string, string> = {
    '../../adr/entity-model.mdx#context': '/adr/entity-model#context',
    '/core/model/schema#schema-4': '/core/model/schema#schema-4',
  };
  const markdown = linkToMarkdown(
    '[ADR](../../adr/entity-model.mdx#context)、[SCHEMA-4](/core/model/schema#schema-4)、'
      + '[本页](#members)、[Mach](https://github.com/apple)、[缺失](./missing.mdx)',
    (href) => pages[href],
  );
  assert.equal(markdown, '[ADR](/adr/entity-model.md#context)、[SCHEMA-4](/core/model/schema.md#schema-4)、'
    + '[本页](#members)、[Mach](https://github.com/apple)、[缺失](./missing.mdx)');
});
