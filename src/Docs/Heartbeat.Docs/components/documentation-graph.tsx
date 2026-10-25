import { asMarkdown } from 'fumadocs-core/server';
import Link from 'next/link';
import { graphMarkdown, sectionOf } from '@/lib/build-graph';
import { getDocumentationGraph } from '@/lib/source';
import { GraphView } from './graph-view';

export function DocumentationGraph() {
  const graph = getDocumentationGraph();
  if (asMarkdown()) return `${graphMarkdown(graph)}\n\n`;

  return (
    <div className="not-prose my-6 space-y-4">
      <div className="flex flex-wrap items-center gap-4 text-sm text-fd-muted-foreground">
        {graph.sections.map((section) => (
          <span key={section.slug} className="flex items-center gap-2">
            <span className="size-2.5 rounded-full" style={{ backgroundColor: section.color }} />
            {section.name}
          </span>
        ))}
        <span>{graph.nodes.length} 个页面 · {graph.links.length} 条引用</span>
      </div>
      <GraphView graph={graph} />
      <details className="rounded-xl border p-4">
        <summary className="cursor-pointer text-sm font-medium">页面列表</summary>
        <ul className="mt-4 grid gap-2 text-sm sm:grid-cols-2 lg:grid-cols-3">
          {graph.nodes.map((node) => (
            <li key={node.id}>
              <Link href={node.url} className="underline underline-offset-4" title={node.description}>
                {sectionOf(graph, node).name} · {node.text}
              </Link>
            </li>
          ))}
        </ul>
      </details>
    </div>
  );
}
