import { useState, type CSSProperties, type ReactNode } from "react";
import { AppWindow, Boxes, MoreHorizontal, Plus, Undo2 } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import { isObservation, type Observation, type Version } from "@/api/types";
import { DayStrip } from "@/components/stack/bodies";
import { Mark, Token } from "@/components/token/Token";
import { useHighlight, useStack } from "@/components/token/context";
import { useNow } from "@/hooks/useNow";
import {
  installDetails,
  keyOf,
  platformOf,
  type Catalog,
  type Edit,
  type Identifier,
  type Product,
} from "@/lib/applications";
import type { Row } from "@/lib/day";
import { thingColor } from "@/lib/roles";
import { ago, duration, MINUTE } from "@/lib/time";
import { cn } from "@/lib/utils";
import { Anchored, MenuItem, MenuLabel } from "@/components/ui";

/** 正在拖动的线索，以及它现在所在的产品。 */
export interface Dragging {
  identifier: Identifier;
  owner: Product | null;
}

export const DRAG_TYPE = "application/x-orisumi-identifier";

export function ProductMark({ name, size = "md" }: { name: string; size?: "sm" | "md" | "lg" }) {
  return <Mark role="thing" color={thingColor(name)} icon={Boxes} size={size} />;
}

/** 产品列表，带筛选。newName 不为空时，最后一项是用这个名字新建产品。 */
function ProductPicker({
  products,
  exclude,
  onPick,
  onCreate,
  createHint,
}: {
  products: Product[];
  exclude?: string | undefined;
  onPick: (product: Product) => void;
  onCreate?: ((name: string) => void) | undefined;
  createHint?: string | undefined;
}) {
  const [query, setQuery] = useState("");
  const list = products
    .filter((p) => p.version.entityId !== exclude && p.name.toLowerCase().includes(query.trim().toLowerCase()))
    .sort((a, b) => a.name.localeCompare(b.name, "zh"));
  const name = query.trim();
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (list.length === 1) onPick(list[0]!);
        else if (name && onCreate && !list.some((p) => p.name === name)) onCreate(name);
      }}
    >
      <input
        autoFocus
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        placeholder={onCreate ? "产品名，或输入新名字" : "产品名"}
        aria-label="选择产品"
        className="bg-panel placeholder:text-fg-3 mb-1 w-full rounded-lg px-2 py-1.5 text-[13px] outline-none"
      />
      <div className="max-h-56 overflow-y-auto">
        {list.map((product) => (
          <MenuItem key={product.version.entityId} onClick={() => onPick(product)}>
            <ProductMark name={product.name} size="sm" />
            <span className="min-w-0 flex-1 truncate">{product.name}</span>
          </MenuItem>
        ))}
        {onCreate && (
          <MenuItem disabled={!name || list.some((p) => p.name === name)} onClick={() => name && onCreate(name)}>
            <Plus className="text-fg-3 size-4" />
            <span className="min-w-0 flex-1 truncate">
              {name ? `新建「${name}」` : (createHint ?? "输入名字新建产品")}
            </span>
          </MenuItem>
        )}
      </div>
    </form>
  );
}

// —— 线索 ——

/** 一条跨设备线索。可以拖到另一个产品上；点它选择去处。 */
export function ClueChip({
  identifier,
  owner,
  catalog,
  busy,
  onEdit,
  onDrag,
}: {
  identifier: Identifier;
  owner: Product | null;
  catalog: Catalog;
  busy: boolean;
  onEdit: (edit: Edit) => void;
  onDrag: (dragging: Dragging | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  return (
    <Anchored
      open={open}
      onClose={close}
      trigger={
        <button
          type="button"
          draggable={!busy}
          aria-haspopup="dialog"
          aria-expanded={open}
          title="拖到另一个产品，或点击选择去处"
          onClick={() => setOpen((value) => !value)}
          onDragStart={(event) => {
            event.dataTransfer.setData(DRAG_TYPE, keyOf(identifier));
            event.dataTransfer.effectAllowed = "move";
            onDrag({ identifier, owner });
          }}
          onDragEnd={() => onDrag(null)}
          className={cn(
            "clue border-line-2 bg-card hover:border-line-3 inline-flex max-w-full min-w-0 cursor-grab items-center gap-1.5 rounded-md border px-1.5 py-0.5 active:cursor-grabbing",
            open && "border-line-3 bg-panel",
          )}
        >
          <span className="text-fg-3 shrink-0 text-[11px]">{platformOf(identifier)}</span>
          <span className="min-w-0 truncate font-mono text-xs">{identifier.value}</span>
        </button>
      }
    >
      <MenuLabel>把 {identifier.value} 归入</MenuLabel>
      <ProductPicker
        products={catalog.products}
        exclude={owner?.version.entityId}
        onPick={(product) => {
          close();
          onEdit({ kind: "move", identifier, to: product });
        }}
        onCreate={(name) => {
          close();
          onEdit({ kind: "move", identifier, to: { name } });
        }}
      />
      {owner && (
        <>
          <div className="border-line my-1 border-t" />
          <MenuItem
            onClick={() => {
              close();
              onEdit({ kind: "release", identifier });
            }}
          >
            <Undo2 className="text-fg-3 size-4" />
            移出「{owner.name}」，重新识别
          </MenuItem>
        </>
      )}
    </Anchored>
  );
}

/** 拥有这条线索的本机应用，每个一枚小窗口；悬停时页面上它的记号一起亮起，点击打开它的面板。 */
export function Installs({ installs, color = null }: { installs: Version[]; color?: string | null }) {
  const stack = useStack();
  const highlight = useHighlight();
  const details = installDetails(installs);
  if (!installs.length) return <span className="text-fg-3 text-[11px]">还没有安装</span>;
  return (
    <span className="flex shrink-0 items-center gap-0.5" aria-label={`${installs.length} 个安装`}>
      {installs.map((install, i) => (
        <button
          key={install.entityId}
          type="button"
          title={details[i] ?? "本机应用"}
          aria-label={`本机应用 ${details[i] ?? ""}`}
          onClick={() => stack.open(install.entityId, -1)}
          onPointerEnter={() => highlight.set(install.entityId)}
          onPointerLeave={() => highlight.set(null)}
          className={cn(
            "cursor-pointer rounded-[5px] transition-shadow",
            highlight.active === install.entityId && "ring-line-3 ring-1",
          )}
        >
          <Mark role="thing" color={color} icon={AppWindow} size="sm" />
        </button>
      ))}
      {installs.length > 1 && <span className="text-fg-3 ml-0.5 text-[11px] tabular-nums">{installs.length}</span>}
    </span>
  );
}

// —— 产品 ——

/** 引用这个产品各版本的 Observation，按它陈述的产品版本索引。 */
export function useStatements(entityId: string) {
  const source = useSource();
  return useQuery({
    queryKey: ["entity", entityId, "referrers", "all"],
    queryFn: async () => {
      const found = new Map<string, Version<Observation>>();
      let after: string | undefined;
      do {
        const page = await source.listReferrers(entityId, { versions: "all", limit: 200, ...(after ? { after } : {}) });
        for (const { version } of page.referrers)
          if (isObservation(version)) found.set(version.content.contentVersionId, version);
        after = page.next ?? undefined;
      } while (after);
      return found;
    },
    staleTime: 30_000,
  });
}

/** 产品的当前版本由谁写下、什么时候。 */
function Provenance({ product }: { product: Product }) {
  const statements = useStatements(product.version.entityId);
  const now = useNow(60_000);
  const statement = statements.data?.get(product.version.versionId);
  const at = Date.parse(product.version.recordedAt);
  return (
    <p className="text-fg-3 flex min-w-0 items-center gap-1 text-[11px]">
      {statement ? (
        <Token versionId={statement.content.observerVersionId} size="sm" />
      ) : (
        statements.isPending && <span>…</span>
      )}
      <span className="shrink-0">{ago(now - at)}</span>
      {now - at < 10 * MINUTE && <span className="bg-accent-soft text-fg-2 shrink-0 rounded px-1">刚更新</span>}
    </p>
  );
}

export function ProductCard({
  product,
  catalog,
  usage,
  from,
  to,
  dragging,
  busy,
  onEdit,
  onDrag,
}: {
  product: Product;
  catalog: Catalog;
  usage: Row | undefined;
  from: number;
  to: number;
  dragging: Dragging | null;
  busy: boolean;
  onEdit: (edit: Edit) => void;
  onDrag: (dragging: Dragging | null) => void;
}) {
  const stack = useStack();
  const highlight = useHighlight();
  const [over, setOver] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [menu, setMenu] = useState<"closed" | "main" | "merge" | "delete">("closed");
  const entityId = product.version.entityId;
  const target = !!dragging && dragging.owner?.version.entityId !== entityId;
  const color = thingColor(product.name);
  const closeMenu = () => setMenu("closed");

  return (
    <article
      aria-label={`应用产品 ${product.name}`}
      onDragOver={(event) => {
        if (!target || !event.dataTransfer.types.includes(DRAG_TYPE)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(event) => {
        event.preventDefault();
        setOver(false);
        if (dragging && target) onEdit({ kind: "move", identifier: dragging.identifier, to: product });
        onDrag(null);
      }}
      className={cn(
        "bg-card border-line relative rounded-xl border px-4 pt-3 pb-2.5 transition-[box-shadow,opacity,border-color] duration-150",
        highlight.active === entityId && "border-line-3",
        target && "border-dashed",
        over && "ring-2",
        dragging && !target && "opacity-60",
      )}
      style={over ? ({ "--tw-ring-color": color } as CSSProperties) : undefined}
    >
      <header className="flex items-center gap-2">
        <ProductMark name={product.name} />
        {renaming ? (
          <form
            className="min-w-0 flex-1"
            onSubmit={(event) => {
              event.preventDefault();
              const name = String(new FormData(event.currentTarget).get("name") ?? "").trim();
              setRenaming(false);
              if (name && name !== product.name) onEdit({ kind: "rename", product, name });
            }}
          >
            <input
              name="name"
              autoFocus
              defaultValue={product.name}
              aria-label="产品名"
              onBlur={(event) => event.currentTarget.form?.requestSubmit()}
              onKeyDown={(event) => {
                if (event.key !== "Escape") return;
                event.preventDefault();
                setRenaming(false);
              }}
              className="border-line-3 w-full rounded-md border bg-transparent px-1.5 py-0.5 text-[15px] font-medium outline-none"
            />
          </form>
        ) : (
          <button
            type="button"
            onClick={() => stack.open(entityId, -1)}
            onPointerEnter={() => highlight.set(entityId)}
            onPointerLeave={() => highlight.set(null)}
            className="min-w-0 flex-1 cursor-pointer truncate text-left text-[15px] font-medium"
          >
            {product.name}
          </button>
        )}
        <span className="text-fg-2 shrink-0 text-xs tabular-nums">{usage ? duration(usage.total) : ""}</span>
        <Anchored
          align="end"
          open={menu !== "closed"}
          onClose={closeMenu}
          trigger={
            <button
              type="button"
              aria-label={`${product.name} 的操作`}
              disabled={busy}
              onClick={() => setMenu((value) => (value === "closed" ? "main" : "closed"))}
              className="text-fg-3 hover:text-fg hover:bg-panel -mr-1 cursor-pointer rounded-md p-1 disabled:opacity-40"
            >
              <MoreHorizontal className="size-4" />
            </button>
          }
        >
          {menu === "main" && (
            <>
              <MenuItem
                onClick={() => {
                  closeMenu();
                  setRenaming(true);
                }}
              >
                改名
              </MenuItem>
              <MenuItem onClick={() => setMenu("merge")} disabled={catalog.products.length < 2}>
                合并到另一个产品…
              </MenuItem>
              <MenuItem danger onClick={() => setMenu("delete")}>
                删除…
              </MenuItem>
            </>
          )}
          {menu === "merge" && (
            <>
              <MenuLabel>把「{product.name}」的线索全部移到</MenuLabel>
              <ProductPicker
                products={catalog.products}
                exclude={entityId}
                onPick={(into) => {
                  closeMenu();
                  onEdit({ kind: "merge", from: product, into });
                }}
              />
            </>
          )}
          {menu === "delete" && (
            <div className="grid gap-2 p-1.5">
              <p className="text-[13px]">
                删除「{product.name}」？
                {product.identifiers.length > 0 && (
                  <span className="text-fg-2">
                    {" "}
                    它的 {product.identifiers.length} 条线索会交给应用识别 Observer 重新判断。
                  </span>
                )}
              </p>
              <div className="flex justify-end gap-1.5">
                <button
                  type="button"
                  onClick={closeMenu}
                  className="text-fg-2 hover:text-fg cursor-pointer rounded-lg px-2.5 py-1 text-[13px]"
                >
                  取消
                </button>
                <button
                  type="button"
                  autoFocus
                  onClick={() => {
                    closeMenu();
                    onEdit({ kind: "delete", product });
                  }}
                  className="bg-danger cursor-pointer rounded-lg px-2.5 py-1 text-[13px] text-white"
                >
                  删除
                </button>
              </div>
            </div>
          )}
        </Anchored>
      </header>

      <div className={cn("ml-7", !usage && "opacity-40")}>
        <DayStrip segments={usage?.segments ?? []} from={from} to={to} color={color} compact />
      </div>

      {product.identifiers.length ? (
        <ul className="mt-2 grid gap-1.5">
          {product.identifiers.map((identifier) => (
            <li key={keyOf(identifier)} className="flex min-w-0 items-center gap-2">
              <ClueChip
                identifier={identifier}
                owner={product}
                catalog={catalog}
                busy={busy}
                onEdit={onEdit}
                onDrag={onDrag}
              />
              <span className="flex-1" />
              <Installs installs={catalog.installsOf(identifier).map((install) => install.version)} color={color} />
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-fg-3 mt-2 text-xs">没有线索。把线索拖进来，或者删除这个产品。</p>
      )}

      <footer className="border-line mt-2.5 border-t pt-2">
        <Provenance product={product} />
      </footer>
    </article>
  );
}

/** 拖动线索时出现在顶部的两个落点：新建产品，或者移出产品交给应用识别 Observer 重新判断。 */
export function DropBar({
  dragging,
  onNew,
  onRelease,
  onDrag,
}: {
  dragging: Dragging | null;
  onNew: (identifier: Identifier, owner: Product | null) => void;
  onRelease: (identifier: Identifier) => void;
  onDrag: (dragging: Dragging | null) => void;
}) {
  if (!dragging) return null;
  return (
    // 浮在页面上方，拖动开始时不推动下面的卡片。
    <div className="pop fixed inset-x-0 top-4 z-30 mx-auto grid max-w-[1080px] grid-cols-2 gap-3 px-4">
      <Zone
        label="放到这里，新建产品"
        icon={<Plus className="size-4" />}
        onDrop={() => {
          onNew(dragging.identifier, dragging.owner);
          onDrag(null);
        }}
      />
      <Zone
        label={dragging.owner ? `移出「${dragging.owner.name}」，重新识别` : "已经未归属"}
        icon={<Undo2 className="size-4" />}
        disabled={!dragging.owner}
        onDrop={() => {
          onRelease(dragging.identifier);
          onDrag(null);
        }}
      />
    </div>
  );
}

function Zone({
  label,
  icon,
  disabled,
  onDrop,
}: {
  label: string;
  icon: ReactNode;
  disabled?: boolean;
  onDrop: () => void;
}) {
  const [over, setOver] = useState(false);
  return (
    <div
      onDragOver={(event) => {
        if (disabled || !event.dataTransfer.types.includes(DRAG_TYPE)) return;
        event.preventDefault();
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(event) => {
        event.preventDefault();
        setOver(false);
        onDrop();
      }}
      className={cn(
        "bg-card/95 flex items-center justify-center gap-2 rounded-xl border border-dashed px-4 py-5 text-[13px] shadow-[0_12px_32px_-16px_rgb(0_0_0/0.35)] backdrop-blur transition-colors",
        disabled ? "border-line text-fg-3" : over ? "border-line-3 bg-panel text-fg" : "border-line-2 text-fg-2",
      )}
    >
      {icon}
      {label}
    </div>
  );
}
