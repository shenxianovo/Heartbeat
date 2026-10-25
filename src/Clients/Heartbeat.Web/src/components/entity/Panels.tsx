import { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { Version } from "@/api/types";
import { Button, Mono } from "@/components/ui";
import { useReferrers } from "@/hooks/queries";
import { dateTime, parseTime } from "@/lib/time";
import { cn } from "@/lib/utils";
import { changedFields } from "@/lib/versions";
import { VersionChip } from "./VersionChip";

const ROW = 34;

/** 版本：从新到旧，每行写出相对前一版变化的字段。版本多时只渲染可见的行。 */
export function VersionList({
  chain,
  selected,
  onSelect,
}: {
  chain: Version[];
  selected: string;
  onSelect: (versionId: string) => void;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const rows = [...chain].reverse();
  // 项目没有启用 React Compiler，这条提示不影响渲染。
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scroller.current,
    estimateSize: () => ROW,
    overscan: 8,
  });

  return (
    <div ref={scroller} className="border-line max-h-[340px] overflow-y-auto border-t">
      <ol aria-label="版本" className="relative" style={{ height: virtualizer.getTotalSize() }}>
        {virtualizer.getVirtualItems().map((item) => {
          const version = rows[item.index]!;
          const number = chain.length - item.index;
          const previous = chain[number - 2];
          const changes = changedFields(previous, version);
          const recordedAt = parseTime(version.recordedAt);
          return (
            <li key={version.versionId} className="absolute inset-x-0" style={{ top: item.start, height: ROW }}>
              <button
                type="button"
                aria-current={version.versionId === selected}
                onClick={() => onSelect(version.versionId)}
                className={cn(
                  "border-line flex h-full w-full cursor-pointer items-baseline gap-3 border-b px-2 pt-2 text-left text-[13px]",
                  version.versionId === selected ? "bg-panel" : "hover:bg-panel/60",
                )}
              >
                <Mono className="w-12 shrink-0">v{number}</Mono>
                <Mono className="text-fg-3 shrink-0">
                  {recordedAt === null ? version.recordedAt : dateTime(recordedAt)}
                </Mono>
                <span className="text-fg-3 truncate text-xs">
                  {previous ? changes.join(", ") || "内容相同" : "首版"}
                </span>
              </button>
            </li>
          );
        })}
      </ol>
    </div>
  );
}

/** 被引用：直接引用这个实体的版本和引用位置，分页加载。 */
export function ReferrerList({ entityId }: { entityId: string }) {
  const referrers = useReferrers(entityId);
  const items = referrers.data?.pages.flatMap((page) => page.referrers) ?? [];

  if (referrers.isPending) return <p className="text-fg-3 text-xs">读取中</p>;
  if (referrers.isError) return <p className="text-danger text-xs">{String((referrers.error as Error).message)}</p>;
  if (!items.length) return <p className="text-fg-3 border-line border-t pt-2 text-xs">没有其他版本引用它</p>;

  return (
    <div className="border-line border-t">
      <ul aria-label="被引用">
        {items.map((referrer) => (
          <li
            key={referrer.version.versionId}
            className="border-line flex flex-wrap items-center gap-3 border-b py-1.5"
          >
            <VersionChip versionId={referrer.version.versionId} />
            <span className="flex-1" />
            <Mono className="text-fg-3">{referrer.references.map((r) => r.path).join(" ")}</Mono>
          </li>
        ))}
      </ul>
      {referrers.hasNextPage && (
        <div className="pt-2">
          <Button onClick={() => void referrers.fetchNextPage()} disabled={referrers.isFetchingNextPage}>
            加载更多
          </Button>
        </div>
      )}
    </div>
  );
}

/** 删除确认：写明会一起移除全部版本。 */
export function DeleteDialog({
  open,
  onClose,
  onConfirm,
  busy,
  error,
}: {
  open: boolean;
  onClose: () => void;
  onConfirm: () => void;
  busy: boolean;
  error: string | null;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [shown, setShown] = useState(false);

  useEffect(() => {
    const element = dialog.current;
    if (!element) return;
    if (open && !element.open) {
      element.showModal?.();
      setShown(true);
    } else if (!open && element.open) {
      element.close();
      setShown(false);
    }
  }, [open]);

  return (
    <dialog
      ref={dialog}
      onClose={onClose}
      aria-labelledby="delete-title"
      className="bg-card text-fg border-line m-auto w-[min(420px,calc(100%-32px))] rounded-xl border p-5"
    >
      {shown && (
        <>
          <h2 id="delete-title" className="text-[15px] font-medium">
            删除这个实体
          </h2>
          <p className="text-fg-2 mt-2 text-[13px]">
            它的全部版本会一起移除。引用过它的版本保留，追溯到这里时显示不可读取。
          </p>
          {error && (
            <p role="alert" className="text-danger mt-2 text-xs">
              {error}
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button onClick={onClose}>取消</Button>
            <Button danger onClick={onConfirm} disabled={busy}>
              删除
            </Button>
          </div>
        </>
      )}
    </dialog>
  );
}
