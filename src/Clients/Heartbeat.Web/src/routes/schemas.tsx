import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import type { EntitySchema, Version } from "@/api/types";
import { Card, Message, Mono } from "@/components/ui";

export const Route = createFileRoute("/schemas")({
  component: SchemasPage,
});

function SchemasPage() {
  const source = useSource();
  const schemas = useQuery({
    queryKey: ["entities", "entity_schema"],
    queryFn: () =>
      readAllPages((after) =>
        source.listEntities({ category: "entity_schema", limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Version<EntitySchema>[]>,
  });

  if (schemas.isPending) return <Message title="读取中" />;
  if (schemas.isError) return <Message title="读不到 Schema" detail={schemas.error.message} />;
  if (!schemas.data.length) return <Message title="还没有 Schema" />;

  const rows = [...schemas.data].sort((a, b) => a.content.displayName.localeCompare(b.content.displayName, "zh"));
  return (
    <Card>
      <ul aria-label="Schema">
        {rows.map((schema) => (
          <li key={schema.entityId} className="border-line border-b last:border-b-0">
            <Link
              to="/entities/$entityId"
              params={{ entityId: schema.entityId }}
              className="hover:bg-panel/60 -mx-2 flex flex-wrap items-baseline gap-x-4 gap-y-0.5 rounded-md px-2 py-2 text-[13px]"
            >
              <span className="min-w-40">{schema.content.displayName}</span>
              <Mono className="text-fg-3 flex-1">{schema.content.resourceName}</Mono>
              <span className="text-fg-3 text-xs">{Object.keys(schema.content.fields).length} 个字段</span>
            </Link>
          </li>
        ))}
      </ul>
    </Card>
  );
}
