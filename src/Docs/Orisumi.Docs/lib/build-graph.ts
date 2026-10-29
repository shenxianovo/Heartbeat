/** The sidebar sections shown in the graph, by the first segment of their URLs. The order is the legend order. */
export const graphSections = ['core', 'adr', 'observers', 'hosting'];

/** Pages left out of the graph with everything below them: the example pages of the Document Template. */
export const graphExcludedPages = ['/observers/template/example-observer'];

// Node colors, assigned to the sections in order.
const palette = ['#14b8a6', '#8b5cf6', '#f59e0b', '#3b82f6', '#ec4899', '#84cc16'];

export interface Section {
  slug: string;
  name: string;
}

export interface GraphSection extends Section {
  color: string;
}

export interface GraphNode {
  id: string;
  url: string;
  text: string;
  description?: string;
  section: string;
}

export interface Graph {
  sections: GraphSection[];
  nodes: GraphNode[];
  links: { source: string; target: string }[];
}

export interface GraphPage {
  url: string;
  path: string;
  data: {
    title: string;
    description?: string;
    extractedReferences?: { href: string }[];
  };
}

export function buildGraph(
  pages: GraphPage[],
  sections: Section[],
  resolve: (href: string, from: GraphPage) => string | undefined,
): Graph {
  const colored = sections.map((section, index) => ({ ...section, color: palette[index % palette.length] }));
  const slugs = new Set(sections.map((section) => section.slug));
  const excluded = (url: string) => graphExcludedPages.some((prefix) => url === prefix || url.startsWith(`${prefix}/`));
  const selected = pages.filter((page) => slugs.has(page.url.split('/')[1]) && !excluded(page.url));
  const nodes = selected.map((page): GraphNode => ({
    id: page.url,
    url: page.url,
    text: page.data.title,
    description: page.data.description,
    section: page.url.split('/')[1],
  }));
  const urls = new Set(nodes.map((node) => node.url));
  const seen = new Set<string>();
  const links: Graph['links'] = [];

  for (const page of selected) {
    for (const reference of page.data.extractedReferences ?? []) {
      const target = resolve(reference.href, page);
      if (!target || target === page.url || !urls.has(target)) continue;
      const key = `${page.url}\0${target}`;
      if (seen.has(key)) continue;
      seen.add(key);
      links.push({ source: page.url, target });
    }
  }

  return { sections: colored, nodes, links };
}

export function sectionOf(graph: Graph, node: GraphNode): GraphSection {
  return graph.sections.find((section) => section.slug === node.section)!;
}

function sectionName(graph: Graph, node: GraphNode): string {
  return sectionOf(graph, node).name;
}

export function graphMarkdown(graph: Graph): string {
  const nodes = new Map(graph.nodes.map((node) => [node.id, node]));
  const label = (node: GraphNode) => `${sectionName(graph, node)} · ${node.text}`;
  return graph.nodes.map((node) => {
    const references = graph.links.filter((link) => link.source === node.id).map((link) => {
      const target = nodes.get(link.target)!;
      return `[${label(target)}](${target.url})`;
    });
    return `### ${sectionName(graph, node)} · [${node.text}](${node.url})\n\n${references.length ? `引用：${references.join('、')}` : '没有引用范围内的其他页面。'}`;
  }).join('\n\n');
}
