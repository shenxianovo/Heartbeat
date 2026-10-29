import { asMarkdown } from 'fumadocs-core/server';
import contracts from '@/content/docs/core/model/relations.json';
import model from '@/content/docs/observers/relations.json';
import { Mermaid } from './mermaid';

type NodeId = keyof typeof model.nodes;
type FieldId = keyof typeof model.fields;
type ContractId = keyof typeof contracts.nodes;

export function ObserverStructure({ node }: { node: NodeId }) {
  const current = model.nodes[node];

  if (asMarkdown()) {
    const interfaces = current.interfaces.map((id) => {
      const contract = contracts.nodes[id as ContractId];
      return `[${contract.label}](${contract.path})`;
    });
    const fields = current.fields.map((id) => {
      const [name, note] = model.fields[id as FieldId].label.split('<br/>');
      return note ? `\`${name}\`（${note}）` : `\`${name}\``;
    });
    return [
      interfaces.length > 0 && `- 实现：${interfaces.join('、')}`,
      fields.length > 0 && `- 字段：${fields.join('、')}`,
    ].filter(Boolean).join('\n') + '\n\n';
  }

  const chart = [
    'flowchart LR',
    `SELF["${current.label}"]:::entity`,
    `click SELF "${current.path}"`,
  ];
  const schemaPath = 'schemaPath' in current ? current.schemaPath : undefined;

  for (const id of current.interfaces) {
    const contract = contracts.nodes[id as ContractId];
    chart.push(
      `${id}{{"${contract.label}"}}:::capability`,
      `${id} -. 实现 .- SELF`,
      `click ${id} "${contract.path}" "阅读 ${contract.label}"`,
    );
  }
  for (const id of current.fields) {
    const field = model.fields[id as FieldId];
    chart.push(`F_${id}["${field.label}"]:::field`, `SELF --- F_${id}`);
    if (schemaPath) chart.push(`click F_${id} "${schemaPath}"`);
  }
  chart.push(
    'classDef capability fill:#E8F1FF,stroke:#376BB0,color:#183A64',
    'classDef entity fill:#E6F5EC,stroke:#37805A,color:#204B35,stroke-width:4px',
    'classDef field fill:#F1EAFE,stroke:#7544A6,color:#35204D',
  );

  return <Mermaid chart={chart.join('\n')} />;
}
