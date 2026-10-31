import { AppWindow, Boxes } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import { isObservation, type Observation, type Version } from "@/api/types";
import { Mark, Token } from "@/components/token/Token";
import { useHighlight, useStack } from "@/components/token/context";
import { useThingImage } from "@/hooks/useIcons";
import { useNow } from "@/hooks/useNow";
import {
  installDetails,
  latestMembership,
  LOCAL_APPLICATIONS,
  PRODUCTS,
  keyOf,
  platformOf,
  type Catalog,
  type Identifier,
  type Membership,
  type Product,
} from "@/lib/applications";
import type { Row } from "@/lib/day";
import { thingColor } from "@/lib/roles";
import { ago, duration, MINUTE } from "@/lib/time";
import { cn } from "@/lib/utils";

/** 产品的记号：有图标时用它的本机应用最新的图标，还没有保存的产品用类型图标。 */
export function ProductMark({
  name,
  version,
  size = "md",
}: {
  name: string;
  version?: Version | undefined;
  size?: "sm" | "md" | "lg";
}) {
  const image = useThingImage(version, version ? PRODUCTS : null);
  return <Mark role="thing" color={thingColor(name)} icon={Boxes} image={image} size={size} />;
}

function InstallMark({ install, color }: { install: Version; color: string | null }) {
  const image = useThingImage(install, LOCAL_APPLICATIONS);
  return <Mark role="thing" color={color} icon={AppWindow} image={image} size="sm" />;
}

// —— 线索 ——

/** 一条跨设备线索。已归属的线索点开它的线索归属。 */
export function ClueChip({ identifier, membership }: { identifier: Identifier; membership?: Membership }) {
  const stack = useStack();
  const body = (
    <>
      <span className="text-fg-3 shrink-0 text-[11px]">{platformOf(identifier)}</span>
      <span className="min-w-0 truncate font-mono text-xs">{identifier.value}</span>
    </>
  );
  const style =
    "border-line-2 bg-card inline-flex max-w-full min-w-0 items-center gap-1.5 rounded-md border px-1.5 py-0.5";
  if (!membership) return <span className={style}>{body}</span>;
  return (
    <button
      type="button"
      title="查看这条线索归属"
      onClick={() => stack.open(membership.version.entityId, -1)}
      className={cn(style, "hover:border-line-3 cursor-pointer")}
    >
      {body}
    </button>
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
          <InstallMark install={install} color={color} />
        </button>
      ))}
      {installs.length > 1 && <span className="text-fg-3 ml-0.5 text-[11px] tabular-nums">{installs.length}</span>}
    </span>
  );
}

// —— 产品 ——

/** 引用这个实体各版本的 Observation，按它陈述的版本索引。 */
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

/** 产品最近一次判断由谁写下、什么时候。 */
function Provenance({ product }: { product: Product }) {
  const latest = latestMembership(product);
  const statements = useStatements(latest.version.entityId);
  const now = useNow(60_000);
  const statement = statements.data?.get(latest.version.versionId);
  const at = Date.parse(latest.version.recordedAt);
  return (
    <p className="text-fg-3 -ml-1 flex min-w-0 items-center gap-1 text-[11px]">
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

/**
 * 产品表的列：名字和最近的判断、今天的用量条、时长、线索与安装。
 * 宽屏时每个产品一行；窄屏时用量条和线索各占一行。
 */
export const PRODUCT_GRID =
  "grid grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-x-4 gap-y-2 lg:grid-cols-[minmax(150px,200px)_minmax(160px,1fr)_64px_minmax(260px,340px)]";

/** 表头：列名，用量列写出 0、6、12、18、24 点，与每一行的用量条对齐。 */
export function ProductHeader() {
  return (
    <div className={cn(PRODUCT_GRID, "text-fg-3 border-line border-b px-4 pb-2 text-[11px] max-lg:hidden")}>
      <span>产品</span>
      <span className="relative h-4 tabular-nums">
        {[0, 6, 12, 18, 24].map((hour) => (
          <span
            key={hour}
            className="absolute top-0"
            style={{ left: `${(hour / 24) * 100}%`, translate: hour === 0 ? "0" : hour === 24 ? "-100%" : "-50%" }}
          >
            {hour}
          </span>
        ))}
      </span>
      <span className="text-right">今天</span>
      <span>线索与安装</span>
    </div>
  );
}

/** 一天的用量条：0 点到 24 点，6、12、18 点各有一道细线，现在的位置是一道竖线。 */
function UsageStrip({
  segments,
  from,
  to,
  now,
  color,
}: {
  segments: readonly Pick<Row["segments"][number], "start" | "end">[];
  from: number;
  to: number;
  now: number;
  color: string;
}) {
  const span = to - from;
  const at = (t: number) => `${((t - from) / span) * 100}%`;
  return (
    <div className="bg-line relative h-2.5 overflow-hidden rounded-full">
      {[6, 12, 18].map((hour) => (
        <span key={hour} className="bg-card absolute inset-y-0 w-px" style={{ left: `${(hour / 24) * 100}%` }} />
      ))}
      {segments.map((segment, i) => (
        <span
          key={i}
          className="absolute inset-y-0"
          style={{
            left: at(segment.start),
            width: `max(1.5px, ${((segment.end - segment.start) / span) * 100}%)`,
            background: color,
          }}
        />
      ))}
      {now > from && now < to && (
        <span className="bg-fg-3 absolute inset-y-0 w-px opacity-60" style={{ left: at(now) }} />
      )}
    </div>
  );
}

/** 一个应用产品占表里的一行。悬停时它在面板和别处的记号一起亮起。 */
export function ProductRow({
  product,
  catalog,
  usage,
  from,
  to,
  now,
}: {
  product: Product;
  catalog: Catalog;
  usage: Row | undefined;
  from: number;
  to: number;
  now: number;
}) {
  const stack = useStack();
  const highlight = useHighlight();
  const entityId = product.version.entityId;
  const color = thingColor(product.name);

  return (
    <article
      aria-label={`应用产品 ${product.name}`}
      onPointerEnter={() => highlight.set(entityId)}
      onPointerLeave={() => highlight.set(null)}
      className={cn(
        PRODUCT_GRID,
        "relative rounded-xl px-4 py-2.5 transition-colors duration-150",
        highlight.active === entityId ? "bg-accent-soft" : "hover:bg-panel/60",
      )}
    >
      <div className="flex min-w-0 items-center gap-2.5">
        <ProductMark name={product.name} version={product.version} />
        <div className="min-w-0 flex-1">
          <button
            type="button"
            onClick={() => stack.open(entityId, -1)}
            className="block max-w-full cursor-pointer truncate text-left text-[14px] leading-snug font-medium hover:underline hover:underline-offset-4"
          >
            {product.name}
          </button>
          <Provenance product={product} />
        </div>
      </div>

      <div className={cn("max-lg:order-last max-lg:col-span-3", !usage && "opacity-50")}>
        <UsageStrip segments={usage?.segments ?? []} from={from} to={to} now={now} color={color} />
      </div>

      <span className="text-fg-2 text-right text-xs tabular-nums">{usage ? duration(usage.total) : "—"}</span>

      <ul className="grid min-w-0 gap-1 max-lg:order-last max-lg:col-span-3">
        {product.memberships.map((membership) => (
          <li key={keyOf(membership.identifier)} className="flex min-w-0 items-center gap-2">
            <ClueChip identifier={membership.identifier} membership={membership} />
            <span className="flex-1" />
            <Installs
              installs={catalog.installsOf(membership.identifier).map((install) => install.version)}
              color={color}
            />
          </li>
        ))}
      </ul>
    </article>
  );
}
