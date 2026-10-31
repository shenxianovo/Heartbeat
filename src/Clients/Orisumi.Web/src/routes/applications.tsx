import { useMemo, useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { Search } from "lucide-react";
import { ClueChip, Installs, ProductHeader, ProductRow } from "@/components/applications/parts";
import { Token } from "@/components/token/Token";
import { Card, Message, PageHeader } from "@/components/ui";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { useNow } from "@/hooks/useNow";
import type { Version } from "@/api/types";
import { keyOf, unassigned, type Catalog, type Identifier } from "@/lib/applications";
import { groupThings, summarize, type Row } from "@/lib/day";
import { dayRange } from "@/lib/time";

export const Route = createFileRoute("/applications")({ component: ApplicationsPage });

/**
 * 应用：一张产品表，每个有线索归属的产品一行，写出今天的用量、它的跨设备线索和归到它的安装。
 * 点线索进入它的线索归属，判错时在实体页删除。
 */
function ApplicationsPage() {
  const catalog = useCatalog();
  const now = useNow(60_000);
  const day = useDay(dayRange(now).key);
  const [query, setQuery] = useState("");

  const usage = useMemo(() => {
    if (!catalog.data || !day.data) return new Map<string, Row>();
    const experiences = groupThings(day.data.experiences, productGroup(catalog.data));
    const summary = summarize(experiences, [], day.range.start, day.range.end);
    return new Map(summary.things.map((row) => [row.key, row]));
  }, [catalog.data, day.data, day.range.start, day.range.end]);

  if (catalog.error && !catalog.data)
    return <Message title="读不到应用" detail={(catalog.error as Error).message} mascot />;
  if (!catalog.data) return <Message title="正在读取应用" />;

  const data = catalog.data;
  const loose = unassigned(data);
  const needle = query.trim().toLowerCase();
  const matches = (identifier: Identifier) => identifier.value.toLowerCase().includes(needle);
  const products = [...data.products]
    .filter((p) => !needle || p.name.toLowerCase().includes(needle) || p.memberships.some((m) => matches(m.identifier)))
    .sort((a, b) => a.name.localeCompare(b.name, "zh"));
  const looseClues = loose.clues.filter((clue) => !needle || matches(clue));

  if (!data.products.length && !data.installs.length)
    return (
      <Message
        title="还没有本机应用"
        detail="前台状态 Observer 记下应用后，应用识别 Observer 会把它们归到产品"
        mascot
      />
    );

  return (
    <section>
      <PageHeader
        title="应用"
        summary={
          <p>
            {data.products.length} 个产品 · {data.installs.length} 个安装
            {loose.clues.length > 0 && ` · ${loose.clues.length} 条线索未归属`}
          </p>
        }
        actions={
          <label className="bg-card border-line focus-within:border-line-3 flex w-full items-center gap-1.5 rounded-lg border px-2.5 py-1.5 sm:w-64">
            <Search className="text-fg-3 size-3.5" aria-hidden="true" />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="筛选产品或线索"
              aria-label="筛选产品或线索"
              className="placeholder:text-fg-3 min-w-0 flex-1 bg-transparent text-[13px] outline-none"
            />
          </label>
        }
      />

      {products.length > 0 ? (
        <Card className="rise px-2 pt-3 pb-2">
          <ProductHeader />
          <div className="divide-line mt-1 grid divide-y">
            {products.map((product) => (
              <ProductRow
                key={product.version.entityId}
                product={product}
                catalog={data}
                usage={usage.get(product.version.entityId)}
                from={day.range.start}
                to={day.range.end}
                now={now}
              />
            ))}
          </div>
        </Card>
      ) : (
        needle && <p className="text-fg-3 py-8 text-center text-[13px]">没有符合「{query.trim()}」的产品</p>
      )}

      {(looseClues.length > 0 || (!needle && loose.withoutClues.length > 0)) && (
        <Unassigned
          clues={looseClues}
          withoutClues={needle ? [] : loose.withoutClues.map((install) => install.version)}
          catalog={data}
        />
      )}
    </section>
  );
}

/** 还没有归入产品的线索，以及只有本机线索、归不进产品的安装。 */
function Unassigned({
  clues,
  withoutClues,
  catalog,
}: {
  clues: Identifier[];
  withoutClues: Version[];
  catalog: Catalog;
}) {
  return (
    <section aria-label="未归属" className="border-line-2 mt-6 rounded-2xl border border-dashed px-5 py-4">
      <h2 className="text-[13px] font-medium">未归属</h2>
      {clues.length > 0 && (
        <>
          <p className="text-fg-3 mt-0.5 mb-2 text-xs">应用识别 Observer 还没有认出这些线索。</p>
          <ul className="grid gap-1.5 md:grid-cols-2 md:gap-x-6">
            {clues.map((identifier) => (
              <li key={keyOf(identifier)} className="flex min-w-0 items-center gap-2">
                <ClueChip identifier={identifier} />
                <span className="flex-1" />
                <Installs installs={catalog.installsOf(identifier).map((install) => install.version)} />
              </li>
            ))}
          </ul>
        </>
      )}
      {withoutClues.length > 0 && (
        <div className="mt-3">
          <p className="text-fg-3 mb-1.5 text-xs">这些安装只有本机线索，归不进产品</p>
          <div className="flex flex-wrap gap-1">
            {withoutClues.map((install) => (
              <Token key={install.entityId} version={install} size="sm" />
            ))}
          </div>
        </div>
      )}
    </section>
  );
}
