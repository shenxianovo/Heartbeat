import assert from 'node:assert/strict';
import { test } from 'node:test';
import { posix } from 'node:path';
import { loader } from 'fumadocs-core/source';
import { buildGraph, graphMarkdown, sectionOf, type GraphPage } from '../lib/build-graph.ts';

function page(url: string, references: string[] = []): GraphPage {
  return { url, path: `${url.slice(1)}.mdx`, data: { title: url, extractedReferences: references.map((href) => ({ href })) } };
}

const sections = [{ slug: 'core', name: 'Core' }, { slug: 'adr', name: 'ADR' }];

test('graph keeps pages of the given sections, merges chapter references and preserves direction', () => {
  const graph = buildGraph([
    page('/core/model/observation', ['/adr/entity-model#context', '/adr/entity-model#decision', '/core/model/observation#time', '/api', '/missing', 'https://example.com']),
    page('/adr/entity-model', ['/core/model/observation']),
    page('/core/isolated'),
    page('/api', ['/adr/entity-model']),
    page('/core-extra'),
  ], sections, (href) => href.split('#')[0]);
  assert.deepEqual(graph.nodes.map((node) => [node.id, node.section]), [
    ['/core/model/observation', 'core'], ['/adr/entity-model', 'adr'], ['/core/isolated', 'core'],
  ]);
  assert.deepEqual(graph.links, [
    { source: '/core/model/observation', target: '/adr/entity-model' },
    { source: '/adr/entity-model', target: '/core/model/observation' },
  ]);
});

test('sections keep their order and names, and each gets its own color', () => {
  const graph = buildGraph([page('/observers/desktop'), page('/core')], [...sections, { slug: 'observers', name: 'Observers' }], (href) => href);
  assert.deepEqual(graph.sections.map((section) => section.name), ['Core', 'ADR', 'Observers']);
  assert.equal(new Set(graph.sections.map((section) => section.color)).size, 3);
  assert.equal(sectionOf(graph, graph.nodes[0]).name, 'Observers');
});

test('example pages of the Document Template are left out', () => {
  const graph = buildGraph([
    page('/observers/template', ['/observers/template/example-observer']),
    page('/observers/template/example-observer'),
    page('/observers/template/example-observer/example-entity'),
    page('/observers/template/example-observer-guide'),
  ], [{ slug: 'observers', name: 'Observers' }], (href) => href);
  assert.deepEqual(graph.nodes.map((node) => node.id), ['/observers/template', '/observers/template/example-observer-guide']);
  assert.deepEqual(graph.links, []);
});

test('relative references are resolved from their owning page', () => {
  const pages = [page('/core/model/observation', ['../../adr/entity-model.mdx#decision']), page('/adr/entity-model')];
  const source = loader({ files: pages.map((page) => ({ type: 'page' as const, path: page.path, data: page.data })) }, { baseUrl: '/' });
  const graph = buildGraph(pages, sections, (href, from) => source.getPageByHref(href, { dir: posix.dirname(from.path) })?.page.url);
  assert.deepEqual(graph.links, [{ source: '/core/model/observation', target: '/adr/entity-model' }]);
});

test('Markdown export includes linked pages and directed references, including isolated pages', () => {
  const graph = buildGraph([page('/core', ['/adr']), page('/adr'), page('/core/isolated')], sections, (href) => href);
  const markdown = graphMarkdown(graph);
  assert.match(markdown, /### Core · \[\/core\]\(\/core\)\n\n引用：\[ADR · \/adr\]\(\/adr\)/);
  assert.match(markdown, /### Core · \[\/core\/isolated\]\(\/core\/isolated\)/);
  assert.equal(graphMarkdown({ sections: [], nodes: [], links: [] }), '');
});
