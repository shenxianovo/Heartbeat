import { useEffect, useMemo, useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { Search, X } from "lucide-react";
import { ClueChip, DropBar, Installs, ProductCard, ProductMark, type Dragging } from "@/components/applications/parts";
import { useStack } from "@/components/token/context";
import { Token } from "@/components/token/Token";
import { Message } from "@/components/ui";
import { editError, productGroup, useApplicationEdit, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { useNow } from "@/hooks/useNow";
import type { Version } from "@/api/types";
import { keyOf, unassigned, type Catalog, type Edit, type Identifier } from "@/lib/applications";
import { groupThings, summarize, type Row } from "@/lib/day";
import { dayRange } from "@/lib/time";
import { cn } from "@/lib/utils";

export const Route = createFileRoute("/applications")({ component: ApplicationsPage });

/**
 * 应用：每个应用产品、它的跨设备线索和归到它的安装。线索可以拖到另一个产品上，
 * 也可以点开选择去处；每次修改以用户本人的身份提交。
 */
function ApplicationsPage() {
  const catalog = useCatalog();
  const edit = useApplicationEdit();
  const stack = useStack();
  const now = useNow(60_000);
  const day = useDay(dayRange(now).key);
  const [query, setQuery] = useState("");
  const [dragging, setDragging] = useState<Dragging | null>(null);
  const [naming, setNaming] = useState<{ identifier: Identifier; suggestion: string } | null>(null);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(null);

  const usage = useMemo(() => {
    if (!catalog.data || !day.data) return new Map<string, Row>();
    const experiences = groupThings(day.data.experiences, productGroup(catalog.data));
    const summary = summarize(experiences, [], day.range.start, day.range.end);
    return new Map(summary.things.map((row) => [row.key, row]));
  }, [catalog.data, day.data, day.range.start, day.range.end]);

  useEffect(() => {
    if (!notice || notice.error) return;
    const timer = setTimeout(() => setNotice(null), 4000);
    return () => clearTimeout(timer);
  }, [notice]);

  function apply(change: Edit) {
    setNotice({ text: "正在保存", error: false });
    edit.mutate(change, {
      onSuccess: ({ summary, deleted }) => {
        setNotice({ text: summary, error: false });
        // 删掉的产品，它的面板也收起来。
        const index = stack.ids.findIndex((entityId) => deleted.includes(entityId));
        if (index >= 0) stack.close(index);
      },
      onError: (error) => setNotice({ text: editError(error), error: true }),
    });
  }

  if (catalog.error && !catalog.data)
    return <Message title="读不到应用" detail={(catalog.error as Error).message} mascot />;
  if (!catalog.data) return <Message title="正在读取应用" />;

  const data = catalog.data;
  const loose = unassigned(data);
  const needle = query.trim().toLowerCase();
  const matches = (identifier: Identifier) => identifier.value.toLowerCase().includes(needle);
  const products = [...data.products]
    .filter((p) => !needle || p.name.toLowerCase().includes(needle) || p.identifiers.some(matches))
    .sort((a, b) => a.name.localeCompare(b.name, "zh"));
  const looseClues = loose.clues.filter((clue) => !needle || matches(clue));
  const busy = edit.isPending;

  if (!data.products.length && !data.installs.length)
    return (
      <Message
        title="还没有本机应用"
        detail="前台状态 Observer 记下应用后，应用识别 Observer 会把它们归到产品"
        mascot
      />
    );

  return (
    <section onDragEnd={() => setDragging(null)}>
      <header className="mb-4 flex flex-wrap items-end gap-x-4 gap-y-3">
        <div>
          <h1 className="text-[19px] font-medium">应用</h1>
          <p className="text-fg-2 mt-0.5 text-[13px]">
            {data.products.length} 个产品 · {data.installs.length} 个安装
            {loose.clues.length > 0 && ` · ${loose.clues.length} 条线索未归属`}
          </p>
        </div>
        <span className="flex-1" />
        <label className="bg-panel flex w-full items-center gap-1.5 rounded-lg px-2.5 py-1.5 sm:w-60">
          <Search className="text-fg-3 size-3.5" aria-hidden="true" />
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="筛选产品或线索"
            aria-label="筛选产品或线索"
            className="placeholder:text-fg-3 min-w-0 flex-1 bg-transparent text-[13px] outline-none"
          />
        </label>
      </header>

      <DropBar
        dragging={dragging}
        onDrag={setDragging}
        onNew={(identifier, owner) => setNaming({ identifier, suggestion: owner?.name ?? lastSegment(identifier) })}
        onRelease={(identifier) => apply({ kind: "release", identifier })}
      />

      {naming && (
        <NewProduct
          identifier={naming.identifier}
          suggestion={naming.suggestion}
          onCancel={() => setNaming(null)}
          onCreate={(name) => {
            setNaming(null);
            apply({ kind: "move", identifier: naming.identifier, to: { name } });
          }}
        />
      )}

      {products.length > 0 ? (
        <div className="rise grid gap-3 md:grid-cols-2">
          {products.map((product) => (
            <ProductCard
              key={product.version.entityId}
              product={product}
              catalog={data}
              usage={usage.get(product.version.entityId)}
              from={day.range.start}
              to={day.range.end}
              dragging={dragging}
              busy={busy}
              onEdit={apply}
              onDrag={setDragging}
            />
          ))}
        </div>
      ) : (
        needle && <p className="text-fg-3 py-8 text-center text-[13px]">没有符合「{query.trim()}」的产品</p>
      )}

      {(looseClues.length > 0 || (!needle && loose.withoutClues.length > 0)) && (
        <Unassigned
          clues={looseClues}
          withoutClues={needle ? [] : loose.withoutClues.map((install) => install.version)}
          catalog={data}
          busy={busy}
          onEdit={apply}
          onDrag={setDragging}
        />
      )}

      <p
        role="status"
        aria-live="polite"
        className={cn(
          "pointer-events-none fixed inset-x-0 bottom-5 z-40 flex justify-center transition-opacity duration-200",
          notice ? "opacity-100" : "opacity-0",
        )}
      >
        {notice && (
          <span
            className={cn(
              "pointer-events-auto flex items-center gap-2 rounded-full px-4 py-2 text-[13px] shadow-lg",
              notice.error ? "bg-danger text-white" : "bg-fg text-page",
            )}
          >
            {notice.text}
            {notice.error && (
              <button type="button" aria-label="关闭提示" onClick={() => setNotice(null)} className="cursor-pointer">
                <X className="size-3.5" />
              </button>
            )}
          </span>
        )}
      </p>
    </section>
  );
}

/** 还没有归入产品的线索，以及只有本机线索、归不进产品的安装。 */
function Unassigned({
  clues,
  withoutClues,
  catalog,
  busy,
  onEdit,
  onDrag,
}: {
  clues: Identifier[];
  withoutClues: Version[];
  catalog: Catalog;
  busy: boolean;
  onEdit: (edit: Edit) => void;
  onDrag: (dragging: Dragging | null) => void;
}) {
  return (
    <section aria-label="未归属" className="border-line-2 mt-6 rounded-xl border border-dashed px-4 py-3">
      <h2 className="text-[13px] font-medium">未归属</h2>
      {clues.length > 0 && (
        <>
          <p className="text-fg-3 mt-0.5 mb-2 text-xs">
            应用识别 Observer 还没有认出这些线索。拖到产品上，或点开选择去处。
          </p>
          <ul className="grid gap-1.5 md:grid-cols-2 md:gap-x-6">
            {clues.map((identifier) => (
              <li key={keyOf(identifier)} className="flex min-w-0 items-center gap-2">
                <ClueChip
                  identifier={identifier}
                  owner={null}
                  catalog={catalog}
                  busy={busy}
                  onEdit={onEdit}
                  onDrag={onDrag}
                />
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

/** 把线索拖到“新建产品”后，给新产品起名字。 */
function NewProduct({
  identifier,
  suggestion,
  onCreate,
  onCancel,
}: {
  identifier: Identifier;
  suggestion: string;
  onCreate: (name: string) => void;
  onCancel: () => void;
}) {
  const [name, setName] = useState(suggestion);
  return (
    <form
      className="pop bg-card border-line-3 mb-3 flex flex-wrap items-center gap-2 rounded-xl border px-4 py-3"
      onSubmit={(event) => {
        event.preventDefault();
        if (name.trim()) onCreate(name.trim());
      }}
    >
      <ProductMark name={name || "新产品"} />
      <input
        autoFocus
        value={name}
        onChange={(event) => setName(event.target.value)}
        onFocus={(event) => event.currentTarget.select()}
        onKeyDown={(event) => {
          if (event.key !== "Escape") return;
          event.preventDefault();
          onCancel();
        }}
        aria-label="新产品的名字"
        className="border-line-2 min-w-40 flex-1 rounded-md border bg-transparent px-2 py-1 text-[15px] font-medium outline-none"
      />
      <span className="text-fg-3 text-xs">
        包含 <span className="font-mono">{identifier.value}</span>
      </span>
      <button
        type="button"
        onClick={onCancel}
        className="text-fg-2 hover:text-fg cursor-pointer rounded-lg px-2.5 py-1 text-[13px]"
      >
        取消
      </button>
      <button
        type="submit"
        disabled={!name.trim()}
        className="bg-fg text-page cursor-pointer rounded-lg px-3 py-1 text-[13px] disabled:opacity-40"
      >
        新建产品
      </button>
    </form>
  );
}

/** 线索最后一段，作为新产品名字的初值，例如 com.tencent.mobileqq 是 mobileqq。 */
function lastSegment(identifier: Identifier): string {
  return identifier.value.split(/[./]/).filter(Boolean).at(-1) ?? identifier.value;
}
