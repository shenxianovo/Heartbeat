import model from '@/content/docs/core/model/relations.json';
import { Mermaid } from './mermaid';

type NodeId = keyof typeof model.nodes;

const shapes: Record<string, [string, string]> = {
  hexagon: ['{{', '}}'],
  rectangle: ['[', ']'],
  parallelogram: ['[/', '/]'],
};

const styles = [
  'classDef capability fill:#E8F1FF,stroke:#376BB0,color:#183A64',
  'classDef entity fill:#E6F5EC,stroke:#37805A,color:#204B35',
  'classDef domain fill:#F1EAFE,stroke:#7544A6,color:#35204D',
  'classDef member fill:#F1EAFE,stroke:#7544A6,color:#35204D',
];

export function ModelRelations({ node }: { node?: NodeId }) {
  // Local graphs include only edges incident on the current node, not
  // additional edges between its neighbours.
  const edges = node
    ? model.edges.filter((edge) => edge.from === node || edge.to === node)
    : model.edges;
  const visible = new Set(edges.flatMap((edge) => [edge.from, edge.to]));
  if (node) visible.add(node);
  const nodes = Object.entries(model.nodes).filter(([id]) => visible.has(id));
  const chart = ['flowchart LR'];

  function declareNode([id, info]: (typeof nodes)[number]) {
    const [open, close] = shapes[info.shape];
    return `${id}${open}"${info.label}"${close}:::${info.style}`;
  }

  if (node) {
    chart.push(...nodes.map(declareNode));
  } else {
    for (const [id, label] of Object.entries(model.groups)) {
      chart.push(`subgraph ${id}[${label}]`);
      chart.push(...nodes.filter(([, info]) => info.group === id).map(declareNode));
      chart.push('end');
    }
  }

  for (const edge of edges) {
    chart.push(edge.kind === 'implementation'
      ? `${edge.from} -. ${edge.label} .-> ${edge.to}`
      : `${edge.from} -->|${edge.label}| ${edge.to}`);
  }
  for (const [id, info] of nodes) {
    chart.push(`click ${id} "${info.path}" "阅读 ${info.label}"`);
  }
  chart.push(...styles);
  if (node) chart.push(`style ${node} stroke-width:4px`);

  return <Mermaid chart={chart.join('\n')} />;
}
