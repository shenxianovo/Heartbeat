import { useState, type CSSProperties, type ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import type { FieldDefinition, Version } from "@/api/types";
import { Button, Mono, Sigil } from "@/components/ui";
import { useReferrers, useVersion } from "@/hooks/queries";
import { UNREADABLE } from "@/lib/observers";
import { outgoingReferences } from "@/lib/references";
import { cn } from "@/lib/utils";
import { useVersionKind } from "./VersionChip";

const NODE = 46;
const GAP = 10;
const ROW = NODE + GAP;
const WIRE = 56;
const FOLDED = 8;

/**
 * 引用图：中间是这个版本，左边是它引用的版本，右边是引用这个实体的版本。连线的箭头指向被引用的一方。
 * 节点高度固定，连线按序号算位置，不测量元素。窄屏时三列上下排列，不画连线。
 */
export function ReferenceMap({
  version,
  fields,
  title,
  center,
}: {
  version: Version;
  fields: Record<string, FieldDefinition> | undefined;
  title: string;
  center: ReactNode;
}) {
  const outgoing = outgoingReferences(version, fields);
  const referrers = useReferrers(version.entityId);
  const [expanded, setExpanded] = useState(false);
  const all = referrers.data?.pages.flatMap((page) => page.referrers) ?? [];
  const incoming = expanded ? all : all.slice(0, FOLDED);
  const hidden = all.length - incoming.length;

  const more = hidden > 0 || (expanded && referrers.hasNextPage) ? 1 : 0;
  const rows = Math.max(1, outgoing.length, incoming.length + more);
  const height = rows * ROW - GAP;

  return (
    <div>
      <div className="text-fg-3 mb-2 hidden grid-cols-[minmax(0,1fr)_56px_minmax(160px,220px)_56px_minmax(0,1fr)] text-xs sm:grid">
        <span>它引用的 · {outgoing.length}</span>
        <span />
        <span />
        <span />
        <span className="text-right">
          引用它的 · {referrers.isPending ? "…" : `${all.length}${referrers.hasNextPage ? "+" : ""}`}
        </span>
      </div>
      <div
        className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_56px_minmax(160px,220px)_56px_minmax(0,1fr)] sm:gap-0"
        style={{ "--map-height": `${height}px` } as CSSProperties}
      >
        <Column label="它引用的">
          {outgoing.length ? (
            outgoing.map((ref) => <VersionNode key={ref.path} versionId={ref.versionId} sub={ref.path} />)
          ) : (
            <Placeholder>不引用其他版本</Placeholder>
          )}
        </Column>

        <Wires count={outgoing.length} rows={rows} side="left" />

        <div className="flex items-center sm:h-[var(--map-height)]">
          <div
            aria-label={title}
            className="border-line-3 bg-card relative flex w-full items-center gap-2.5 rounded-xl border px-3 py-2.5"
          >
            {center}
          </div>
        </div>

        <Wires count={incoming.length} extra={more} rows={rows} side="right" />

        <div className="flex flex-col justify-center gap-[10px] sm:h-[var(--map-height)]">
          <span className="text-fg-3 text-xs sm:hidden">引用它的</span>
          {referrers.isPending ? (
            <Placeholder>读取中</Placeholder>
          ) : referrers.isError ? (
            <Placeholder danger>{(referrers.error as Error).message}</Placeholder>
          ) : !all.length ? (
            <Placeholder>没有其他版本引用它</Placeholder>
          ) : (
            <>
              <ul aria-label="被引用" className="flex flex-col gap-[10px]">
                {incoming.map((referrer) => (
                  <li key={referrer.version.versionId}>
                    <VersionNode
                      versionId={referrer.version.versionId}
                      version={referrer.version}
                      sub={referrer.references.map((r) => r.path).join(" ")}
                    />
                  </li>
                ))}
              </ul>
              {hidden > 0 ? (
                <MoreButton onClick={() => setExpanded(true)}>还有 {hidden} 个</MoreButton>
              ) : (
                expanded &&
                referrers.hasNextPage && (
                  <MoreButton onClick={() => void referrers.fetchNextPage()} disabled={referrers.isFetchingNextPage}>
                    加载更多
                  </MoreButton>
                )
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function Column({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col justify-center gap-[10px] sm:h-[var(--map-height)]">
      <span className="text-fg-3 text-xs sm:hidden">{label}</span>
      {children}
    </div>
  );
}

/** 从每个节点连到中间。左边的箭头指向节点（这个版本引用它），右边的箭头指向中间（它引用这个实体）。 */
function Wires({
  count,
  extra = 0,
  rows,
  side,
}: {
  count: number;
  /** 节点下面还排着几行按钮，它们和节点一起居中。 */
  extra?: number;
  rows: number;
  side: "left" | "right";
}) {
  const height = rows * ROW - GAP;
  const offset = (height - ((count + extra) * ROW - GAP)) / 2;
  const middle = height / 2;
  const color = "var(--line-3)";
  const marker = `arrow-${side}`;
  return (
    <svg
      aria-hidden="true"
      className="hidden overflow-visible sm:block"
      width={WIRE}
      height={height}
      viewBox={`0 0 ${WIRE} ${height}`}
    >
      <defs>
        <marker id={marker} viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
          <path d="M0 0 8 4 0 8Z" fill={color} />
        </marker>
      </defs>
      {Array.from({ length: count }, (_, i) => {
        const y = offset + i * ROW + NODE / 2;
        const d =
          side === "left"
            ? `M${WIRE} ${middle} C${WIRE / 2} ${middle} ${WIRE / 2} ${y} 2 ${y}`
            : `M${WIRE - 2} ${y} C${WIRE / 2} ${y} ${WIRE / 2} ${middle} 2 ${middle}`;
        return <path key={i} d={d} fill="none" stroke={color} strokeWidth="1" markerEnd={`url(#${marker})`} />;
      })}
    </svg>
  );
}

/** 图里的一个版本：徽记、类型名和引用位置，点击进入它所属的实体。 */
export function VersionNode({ versionId, version, sub }: { versionId: string; version?: Version; sub: string }) {
  const query = useVersion(version ? null : versionId);
  const data = version ?? query.data;
  const kind = useVersionKind(data);
  if (!version && query.isError) {
    return (
      <div className="border-line-2 text-fg-3 flex h-[46px] items-center gap-2 rounded-xl border border-dashed px-2.5 text-[13px]">
        <span className="min-w-0">
          <span className="block truncate">{UNREADABLE}</span>
          <Mono className="block truncate text-[11px]">{sub}</Mono>
        </span>
      </div>
    );
  }
  return (
    <Link
      to="/versions/$versionId"
      params={{ versionId }}
      className="bg-card border-line hover:border-line-3 flex h-[46px] items-center gap-2 rounded-xl border px-2.5 transition-colors"
    >
      <Sigil tint={kind.tint} icon={kind.icon} text={kind.name} size="sm" />
      <span className="min-w-0">
        <span className="block truncate text-[13px]">{kind.name ?? "…"}</span>
        <Mono className="text-fg-3 block truncate text-[11px]">{sub}</Mono>
      </span>
    </Link>
  );
}

function Placeholder({ children, danger }: { children: ReactNode; danger?: boolean }) {
  return (
    <p
      className={cn(
        "border-line-2 flex h-[46px] items-center justify-center rounded-xl border border-dashed px-3 text-xs",
        danger ? "text-danger" : "text-fg-3",
      )}
    >
      {children}
    </p>
  );
}

function MoreButton({ children, onClick, disabled }: { children: ReactNode; onClick: () => void; disabled?: boolean }) {
  return (
    <div className="flex h-[46px] items-center justify-center">
      <Button onClick={onClick} disabled={disabled}>
        {children}
      </Button>
    </div>
  );
}
