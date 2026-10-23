import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  adrStructure, anchorsOf, deprecatedTerms, findTerm, jsonBlocks, linksOf, ruleHeadings, rulePrefixes, ruleReferences, slugify,
} from '../lib/docs-check.ts';

test('slugs keep CJK text and drop punctuation like github-slugger', () => {
  assert.equal(slugify('API 遥测验证'), 'api-遥测验证');
  assert.equal(slugify('Observers · Integration Guide'), 'observers--integration-guide');
});

test('explicit heading ids replace the slug, and headings in code blocks are ignored', () => {
  const anchors = anchorsOf('## 事实时间 [#fact-time]\n### 时间\n```md\n## 不是标题\n```\n');
  assert.deepEqual([...anchors], ['fact-time', '时间']);
});

test('links inside code are not links', () => {
  const links = linksOf('[A](./a.mdx) `[B](./b.mdx)`\n```\n[C](./c.mdx)\n```\n');
  assert.deepEqual(links.map((link) => link.target), ['./a.mdx']);
});

test('the Core prefix table maps prefixes to pages', () => {
  const prefixes = rulePrefixes('| `ENTITY` | [IEntity](./model/entity.mdx) |\n| `TIME` | [Time](./rules/time.mdx) |\n');
  assert.deepEqual([...prefixes], [['ENTITY', 'model/entity.mdx'], ['TIME', 'rules/time.mdx']]);
});

test('rule headings need their page prefix, a matching anchor and ascending numbers', () => {
  const page = '### TIME-1 a [#time-1]\n### TIME-3 b [#time-3]\n### TIME-2 c [#time-2]\n### TIME-4 d [#time-5]\n### ENTITY-9 e [#entity-9]\n';
  const { rules, findings } = ruleHeadings('time.mdx', page, 'TIME');
  assert.equal(rules.length, 5);
  assert.deepEqual(findings.map((finding) => finding.line), [3, 4, 5]);
});

test('rule references only count known prefixes', () => {
  const found = ruleReferences('见 TIME-4、UTF-8 和 IDENTITY-3。', new Set(['TIME', 'IDENTITY', 'ENTITY']));
  assert.deepEqual(found.map((reference) => reference.id), ['TIME-4', 'IDENTITY-3']);
});

test('deprecated terms come from the Glossary table and ASCII terms match whole words', () => {
  const glossary = '## 时间\n| `x` | y |\n## 不再使用的术语 [#deprecated]\n| 术语 | 替代 |\n| `ITimed`、`OBS-` | z |\n';
  assert.deepEqual(deprecatedTerms(glossary).terms, ['ITimed', 'OBS-']);
  assert.deepEqual(findTerm('OBSERVATION-6 and OBS-6', 'OBS-'), [18]);
  assert.deepEqual(findTerm('不再说采集程序了', '采集程序'), [3]);
});

test('ADRs need the template sections in order', () => {
  const sections = (names: string[]) => names.map((name) => `## ${name} [#x]`).join('\n');
  assert.deepEqual(adrStructure('a.mdx', sections(['背景', '决定', '理由', '代价', '规则位置'])), []);
  assert.deepEqual(adrStructure('a.mdx', sections(['背景', '决定', '规则', '理由', '代价', '规则位置'])), []);
  assert.equal(adrStructure('a.mdx', sections(['背景', '决定', '理由', '影响'])).length, 1);
});

test('JSON examples must parse', () => {
  assert.equal(jsonBlocks('a.md', '```json\n{"a": 1}\n```\n```json\n{"a": }\n```\n').length, 1);
});
