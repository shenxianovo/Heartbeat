import { loader } from 'fumadocs-core/source';
import { lucideIconsPlugin } from 'fumadocs-core/source/lucide-icons';
import { docsRoute } from './routes';
import { defineDocs } from 'fumadocs-mdx/macro';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';
import { openapi } from './openapi';
import { buildGraph, graphSections, type Section } from './build-graph';
import { posix } from 'node:path';

const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
    postprocess: {
      includeProcessedMarkdown: { output: 'function' },
      extractLinkReferences: true,
    },
  },
  meta: {
    schema: metaSchema,
  },
});

export const source = loader(
  {
    docs: docs.toFumadocsSource(),
    openapi: await openapi.staticSource({ baseDir: 'api' }),
  },
  {
    baseUrl: docsRoute,
    plugins: [lucideIconsPlugin(), openapi.loaderPlugin()],
  },
);

type TreeNode = ReturnType<typeof source.getPageTree>['children'][number];

function firstUrl(node: TreeNode): string | undefined {
  if (node.type === 'page') return node.url;
  if (node.type === 'folder') return node.index?.url ?? node.children.map(firstUrl).find(Boolean);
}

/** The sections of the sidebar in order: the root folders, named by their `meta.json` titles. */
export function getSections(): Section[] {
  return source.getPageTree().children.flatMap((node) => {
    const url = node.type === 'folder' && node.root ? firstUrl(node) : undefined;
    return url ? [{ slug: url.split('/')[1], name: String(node.name) }] : [];
  });
}

export function getDocumentationGraph() {
  const sections = getSections();
  return buildGraph(
    source.getPages().filter((page) => page.type === 'docs'),
    graphSections.map((slug) => {
      const section = sections.find((item) => item.slug === slug);
      if (!section) throw new Error(`Graph section "${slug}" is not a section of the sidebar`);
      return section;
    }),
    (href, page) => source.getPageByHref(href, { dir: posix.dirname(page.path) })?.page.url,
  );
}
