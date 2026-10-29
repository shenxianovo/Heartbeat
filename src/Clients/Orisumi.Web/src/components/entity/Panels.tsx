import { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { Version } from "@/api/types";
import { Button, Mono } from "@/components/ui";
import { dateTime, parseTime } from "@/lib/time";
import { cn } from "@/lib/utils";
import { changedFields } from "@/lib/versions";

const ROW = 52;

/** 版本：从新到旧排成一条时间线，每个点写出相对前一版变化的字段。版本多时只渲染可见的行。 */
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
    <div ref={scroller} className="max-h-[416px] overflow-y-auto">
      <ol aria-label="版本" className="relative" style={{ height: virtualizer.getTotalSize() }}>
        {virtualizer.getVirtualItems().map((item) => {
          const version = rows[item.index]!;
          const number = chain.length - item.index;
          const previous = chain[number - 2];
          const changes = changedFields(previous, version);
          const recordedAt = parseTime(version.recordedAt);
          const current = version.versionId === selected;
          const last = item.index === rows.length - 1;
          return (
            <li key={version.versionId} className="absolute inset-x-0" style={{ top: item.start, height: ROW }}>
              <button
                type="button"
                aria-current={current}
                onClick={() => onSelect(version.versionId)}
                className={cn(
                  "group relative flex h-full w-full cursor-pointer gap-3 rounded-xl pr-2 pl-1 text-left",
                  current ? "bg-panel" : "hover:bg-panel/60",
                )}
              >
                <span aria-hidden="true" className="relative flex w-4 shrink-0 justify-center">
                  {!last && <span className="bg-line-2 absolute top-[22px] bottom-[-8px] w-px" />}
                  <span
                    className={cn(
                      "relative mt-[13px] size-2.5 rounded-full ring-2",
                      current ? "bg-fg ring-line-2" : "bg-card ring-line-2 group-hover:ring-line-3",
                    )}
                  />
                </span>
                <span className="min-w-0 flex-1 pt-1.5">
                  <span className="flex items-baseline gap-2">
                    <Mono className={cn("text-[13px]", "text-fg")}>v{number}</Mono>
                    <Mono className="text-fg-3 truncate text-[11px]">
                      {recordedAt === null ? version.recordedAt : dateTime(recordedAt)}
                    </Mono>
                  </span>
                  <span className="text-fg-3 block truncate text-xs">
                    {previous ? changes.join(", ") || "内容相同" : "首版"}
                  </span>
                </span>
              </button>
            </li>
          );
        })}
      </ol>
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
