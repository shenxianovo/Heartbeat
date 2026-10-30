import { useEffect } from "react";
import { Link } from "@tanstack/react-router";
import { X } from "lucide-react";
import { ApiError } from "@/api/client";
import { useEntity } from "@/hooks/queries";
import { useDescribe } from "@/hooks/useEntityInfo";
import { cn } from "@/lib/utils";
import { InfoMark } from "../token/Token";
import { PanelDepth, useHighlight, useStack } from "../token/context";
import { PanelBody } from "./bodies";

/**
 * 右侧堆叠的面板。从记号打开的实体依次排在右边，最后两个展开，更早的收成窄条只露出名字。
 * 沿引用追溯时，走过的路一直留在屏幕上；点窄条回到那一层，Esc 关掉最后一个。
 */
export function Stack() {
  const { ids, close } = useStack();

  useEffect(() => {
    if (!ids.length) return;
    function onKey(event: KeyboardEvent) {
      if (event.key === "Escape" && !event.defaultPrevented) close(ids.length - 1);
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [ids, close]);

  if (!ids.length) return null;
  return (
    <aside aria-label="面板" className="pointer-events-none fixed inset-y-0 right-0 z-30 flex max-w-full justify-end">
      {ids.map((entityId, index) => (
        <Panel
          key={`${index}:${entityId}`}
          entityId={entityId}
          depth={index}
          expanded={index >= ids.length - 2}
          last={index === ids.length - 1}
          onClose={() => close(index)}
          onFocus={() => close(index + 1)}
        />
      ))}
    </aside>
  );
}

function Panel({
  entityId,
  depth,
  expanded,
  last,
  onClose,
  onFocus,
}: {
  entityId: string;
  depth: number;
  expanded: boolean;
  last: boolean;
  onClose: () => void;
  onFocus: () => void;
}) {
  const entity = useEntity(entityId);
  const { info } = useDescribe(entity.data);
  const highlight = useHighlight();
  const status = entity.error instanceof ApiError ? entity.error.status : 0;

  return (
    <section
      aria-label={info ? `${info.typeName} ${info.title}` : "实体"}
      className={cn(
        "panel-enter bg-card border-line pointer-events-auto flex h-full shrink-0 flex-col overflow-hidden border-l",
        "shadow-[-24px_0_48px_-28px_rgb(0_0_0/0.4)] transition-[width] duration-200 ease-out",
        expanded ? "w-[min(420px,100vw)]" : "w-11 max-md:hidden",
        !last && expanded && "max-lg:hidden",
      )}
      onPointerEnter={() => highlight.set(entityId)}
      onPointerLeave={() => highlight.set(null)}
    >
      {!expanded ? (
        <button
          type="button"
          onClick={onFocus}
          title={info?.title}
          className="hover:bg-panel flex h-full w-full cursor-pointer flex-col items-center gap-2 py-3"
        >
          {info && <InfoMark info={info} size="md" />}
          <span className="text-fg-2 text-xs [writing-mode:vertical-rl]">{info?.title ?? "…"}</span>
        </button>
      ) : (
        <>
          <header className="border-line flex items-start gap-3 border-b px-5 pt-5 pb-4">
            {info ? <InfoMark info={info} size="lg" /> : <span className="bg-panel size-10 rounded-[10px]" />}
            <div className="min-w-0 flex-1">
              <p className="text-fg-3 text-xs">{info ? `${info.roleName} · ${info.typeName}` : "读取中"}</p>
              <h2 className="truncate text-[18px] leading-snug font-semibold tracking-[-0.01em] break-all">
                {info?.title ?? "…"}
              </h2>
            </div>
            <button
              type="button"
              aria-label="关闭"
              onClick={onClose}
              className="text-fg-3 hover:text-fg hover:bg-panel -mr-1 cursor-pointer rounded-md p-1"
            >
              <X className="size-4" />
            </button>
          </header>
          <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">
            <PanelDepth depth={depth}>
              {entity.isError ? (
                <p className="text-fg-3 py-10 text-center text-[13px]">
                  {status === 410 ? "这个实体已删除" : status === 404 ? "没有这个实体" : "读不到这个实体"}
                </p>
              ) : info ? (
                <PanelBody info={info} />
              ) : (
                <p className="text-fg-3 py-10 text-center text-[13px]">读取中</p>
              )}
            </PanelDepth>
          </div>
          {info && (
            <footer className="border-line bg-side text-fg-3 flex items-center gap-3 border-t px-5 py-2.5 text-xs">
              <span className="font-mono">…{entityId.slice(-8)}</span>
              <span className="flex-1" />
              {info.role !== "definition" && (
                <Link to="/entities" search={{ root: entityId }} className="hover:text-fg">
                  从这里织
                </Link>
              )}
              <Link to="/entities/$entityId" params={{ entityId }} className="hover:text-fg">
                在检查器中打开 →
              </Link>
            </footer>
          )}
        </>
      )}
    </section>
  );
}
