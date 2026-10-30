import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import { isObservation, type Observation, type Version } from "@/api/types";
import { Fields } from "@/components/stack/bodies";
import { InfoMark, Token } from "@/components/token/Token";
import { useObservationOf, type EntityInfo } from "@/hooks/useEntityInfo";
import { useNow } from "@/hooks/useNow";
import { ago, clock, dayTitle, duration, parseTime } from "@/lib/time";

const OPEN_DELAY = 280;
const GAP = 10;
const MARGIN = 8;

/**
 * 悬停一会儿，在旁边浮出这个实体的预览：名字、角色和类型，经历的事实时间，以及内容的前几个字段。
 * 用键盘聚焦时同样浮出。预览只用来看，点击仍然交给里面的元素。
 */
export function Peek({
  info,
  side = "right",
  footer,
  children,
}: {
  info: EntityInfo | null;
  /** 优先浮在哪一边，放不下时换到另一边。 */
  side?: "left" | "right";
  /** 预览底部的补充，例如引用位置。 */
  footer?: ReactNode;
  children: ReactNode;
}) {
  const anchor = useRef<HTMLDivElement>(null);
  const timer = useRef<number | undefined>(undefined);
  const [open, setOpen] = useState(false);

  useEffect(() => () => window.clearTimeout(timer.current), []);
  useEffect(() => {
    if (!open) return;
    const close = () => setOpen(false);
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && close();
    window.addEventListener("keydown", onKey);
    window.addEventListener("scroll", close, true);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("scroll", close, true);
    };
  }, [open]);

  function show(delay: number) {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setOpen(true), delay);
  }
  function hide() {
    window.clearTimeout(timer.current);
    setOpen(false);
  }

  return (
    <div
      ref={anchor}
      onPointerEnter={(event) => event.pointerType === "mouse" && show(OPEN_DELAY)}
      onPointerLeave={hide}
      onPointerDown={hide}
      onFocus={(event) => event.target.matches(":focus-visible") && show(0)}
      onBlur={hide}
    >
      {children}
      {open &&
        info &&
        createPortal(
          <Card anchor={anchor} side={side}>
            <PeekBody info={info} footer={footer} />
          </Card>,
          document.body,
        )}
    </div>
  );
}

/** 浮层贴着触发它的元素，左右放不下时放到下面；内容读到后高度变化，位置跟着重算。 */
function Card({
  anchor,
  side,
  children,
}: {
  anchor: RefObject<HTMLDivElement | null>;
  side: "left" | "right";
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<{ left: number; top: number } | null>(null);

  useLayoutEffect(() => {
    const card = ref.current;
    if (!card || !anchor.current) return;
    const place = () => {
      const target = anchor.current!.getBoundingClientRect();
      const { width, height } = card.getBoundingClientRect();
      const right = target.right + GAP;
      const left = target.left - GAP - width;
      const fitsRight = right + width <= window.innerWidth - MARGIN;
      const fitsLeft = left >= MARGIN;
      const clampTop = (top: number) => Math.max(MARGIN, Math.min(top, window.innerHeight - height - MARGIN));
      if ((side === "right" && fitsRight) || (side === "left" && !fitsLeft && fitsRight))
        setPosition({ left: right, top: clampTop(target.top) });
      else if (fitsLeft) setPosition({ left, top: clampTop(target.top) });
      else
        setPosition({
          left: Math.max(MARGIN, Math.min(target.left, window.innerWidth - width - MARGIN)),
          top: target.bottom + GAP,
        });
    };
    place();
    const observer = new ResizeObserver(place);
    observer.observe(card);
    return () => observer.disconnect();
  }, [anchor, side]);

  return (
    <div
      ref={ref}
      role="tooltip"
      className="bg-card border-line shadow-float pop pointer-events-none fixed z-40 w-[300px] rounded-xl border p-3.5"
      style={position ?? { left: 0, top: 0, visibility: "hidden" }}
    >
      {children}
    </div>
  );
}

function PeekBody({ info, footer }: { info: EntityInfo; footer: ReactNode }) {
  const { observation, pending } = useObservationOf(info);
  const recordedAt = parseTime(info.version.recordedAt);
  const now = useNow(30_000);
  const content = (info.version.content ?? {}) as Record<string, unknown>;
  // 名字已经写在标题上，字段里不再重复。
  const titleKey = Object.keys(content).find((key) => content[key] === info.title);

  return (
    <div className="grid gap-3 text-[13px]">
      <header className="flex items-center gap-2.5">
        <InfoMark info={info} size="lg" framed />
        <div className="min-w-0">
          <p className="truncate text-[15px] leading-snug font-medium">{info.title}</p>
          <p className="text-fg-3 truncate text-xs">
            {info.roleName} · {info.typeName}
          </p>
        </div>
      </header>

      {observation ? (
        // Observation 的名字就是它的时间，这里只补日期和时长。
        <FactTime observation={observation} brief={observation === info.version} />
      ) : (
        pending && <p className="text-fg-3 text-xs">读取时间…</p>
      )}

      {isObservation(info.version) ? (
        <dl className="grid grid-cols-[4.5rem_minmax(0,1fr)] items-center gap-x-3 gap-y-1">
          <dt className="text-fg-3 text-xs">观测内容</dt>
          <dd className="min-w-0">
            <Token versionId={info.version.content.contentVersionId} size="sm" />
          </dd>
          <dt className="text-fg-3 text-xs">来源</dt>
          <dd className="min-w-0">
            <Token versionId={info.version.content.observerVersionId} size="sm" />
          </dd>
        </dl>
      ) : (
        info.role !== "definition" && (
          <Fields version={info.version} schema={info.schema} skipReferences skip={titleKey} limit={4} />
        )
      )}

      <footer className="border-line text-fg-3 flex items-center gap-2 border-t pt-2 text-xs">
        {footer}
        <span className="flex-1" />
        {recordedAt !== null && <span>{ago(now - recordedAt)}记录</span>}
      </footer>
    </div>
  );
}

function FactTime({ observation, brief }: { observation: Version<Observation>; brief: boolean }) {
  const now = useNow(1000);
  const start = parseTime(observation.content.startAt);
  const end = parseTime(observation.content.endAt);
  if (start === null && end === null) return brief ? null : <p className="text-fg-3">时间未知</p>;
  const span = start !== null && end !== null && end > start;
  const going = span && now - end < 30_000;
  if (brief)
    return (
      <p className="text-fg-2 -mt-1.5 text-xs">
        {dayTitle((start ?? end)!)}
        {span && ` · ${duration(end - start)}`}
        {going && <span className="text-accent"> · 还在继续</span>}
      </p>
    );
  return (
    <div>
      <p className="text-fg-3 text-xs">{dayTitle((start ?? end)!)}</p>
      <p className="text-[17px] leading-tight font-medium tabular-nums">
        {span ? `${clock(start)} – ${clock(end)}` : clock((start ?? end)!)}
        {span && (
          <span className="text-fg-2 ml-2 text-[13px] font-normal">
            {duration(end - start)}
            {going && <span className="text-accent"> · 还在继续</span>}
          </span>
        )}
      </p>
    </div>
  );
}
