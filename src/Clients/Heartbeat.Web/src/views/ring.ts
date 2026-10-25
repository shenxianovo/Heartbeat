import { arc } from "d3-shape";
import { isActive, type ObserverActivity, type Segment } from "@/lib/activity";
import { categoryInfo } from "@/lib/categories";
import { clock, DAY, SECOND } from "@/lib/time";
import { color, esc, label, n, type Renderer } from "./common";

const HEIGHT = 320;
const CY = 158;
const INNER = 50;
const OUTER = 138;
export const MAX_RINGS = 24;
const MAX_RING_WIDTH = 16;
/** 只知道一个边界的 Observation 画成约 4 分钟宽的刻度。 */
const POINT = 4 * 60 * SECOND;

export interface Ring {
  key: string;
  name: string;
  /** 合并的“其他”圈没有单一的 Observer。 */
  observer: ObserverActivity | null;
  segments: Segment[];
}

/** 写入多的靠外。超过 24 圈时保留写入最多的 23 个，其余合并成最内的“其他”。 */
export function ringLayout(observers: ObserverActivity[], max = MAX_RINGS): Ring[] {
  const ranked = observers.filter((o) => o.segments.length).sort((a, b) => b.observationCount - a.observationCount);
  const kept = ranked.length > max ? ranked.slice(0, max - 1) : ranked;
  const rest = ranked.length > max ? ranked.slice(max - 1) : [];
  const rings: Ring[] = kept.map((o) => ({ key: o.key, name: o.name, observer: o, segments: o.segments })).reverse();
  if (rest.length)
    rings.unshift({ key: "other", name: "其他", observer: null, segments: rest.flatMap((o) => o.segments) });
  return rings;
}

/** 表盘：按事实时间展开今天，一个 Observer 一圈，指针指向现在。 */
export const renderRing: Renderer = ({
  width: viewWidth,
  observers,
  now,
  selected,
  reduced,
  dayStart,
  observationCount,
}) => {
  const CX = viewWidth / 2;
  const rings = ringLayout(observers);
  // 圈少时每圈最宽 16，从外圈往里排。
  const width = rings.length ? Math.min(MAX_RING_WIDTH, (OUTER - INNER) / rings.length) : 0;
  const first = OUTER - rings.length * width;
  const angle = (t: number) => ((Math.min(Math.max(t, dayStart), dayStart + DAY) - dayStart) / DAY) * Math.PI * 2;
  const path = arc<{ innerRadius: number; outerRadius: number; startAngle: number; endAngle: number }>();

  let markup = label(viewWidth, 16, "按事实时间", "muted", { anchor: "end" });
  for (const hour of [0, 6, 12, 18]) {
    const a = (hour / 24) * Math.PI * 2;
    markup += label(n(CX + (OUTER + 16) * Math.sin(a)), n(CY - (OUTER + 16) * Math.cos(a) + 4), String(hour), "muted", {
      anchor: "middle",
    });
  }

  rings.forEach((ring, index) => {
    const innerRadius = first + index * width + 0.8;
    const outerRadius = first + (index + 1) * width - 0.8;
    const observer = ring.observer;
    const active = observer ? isActive(observer, now) : false;
    const fill = observer && active ? color(observer) : "var(--fg-3)";
    let inner = `<path d="${path({ innerRadius, outerRadius, startAngle: 0, endAngle: Math.PI * 2 })}" fill="var(--panel)"/>`;
    for (const segment of ring.segments) {
      const live = now - segment.recordedAt < 60 * SECOND;
      let start: number;
      let end: number;
      if (segment.start !== null && segment.end !== null) {
        start = segment.start;
        end = live ? Math.max(segment.end, now) : segment.end;
      } else {
        const point = (segment.start ?? segment.end)!;
        start = point - POINT / 2;
        end = point + POINT / 2;
      }
      const a0 = angle(start);
      const a1 = Math.max(angle(Math.min(end, now)), a0 + 0.004);
      inner += `<path d="${path({ innerRadius, outerRadius, startAngle: a0, endAngle: a1 })}" fill="${fill}" opacity="${active ? 0.8 : 0.45}"/>`;
    }
    if (observer && !reduced) {
      const age = now - observer.lastSeenAt;
      if (age >= 0 && age < SECOND) {
        const a = angle(now);
        const middle = (innerRadius + outerRadius) / 2;
        inner += `<circle cx="${n(middle * Math.sin(a))}" cy="${n(-middle * Math.cos(a))}" r="${n(width / 2 + 3 * (1 - age / SECOND))}" fill="${categoryInfo(observer.category).color}" opacity="${n(1 - age / SECOND)}"/>`;
      }
    }
    const dimmed = selected !== null && selected !== ring.key ? ' opacity="0.2"' : "";
    const key = observer ? ` data-key="${esc(ring.key)}"` : "";
    markup += `<g${key}${dimmed} transform="translate(${CX},${CY})"><title>${esc(ring.name)}</title>${inner}</g>`;
  });

  const hand = angle(now);
  markup += `<line x1="${n(CX + (INNER - 6) * Math.sin(hand))}" y1="${n(CY - (INNER - 6) * Math.cos(hand))}" x2="${n(CX + (OUTER + 4) * Math.sin(hand))}" y2="${n(CY - (OUTER + 4) * Math.cos(hand))}" stroke="var(--fg)" stroke-width="1.2"/>`;
  markup += label(CX, CY + 3, clock(now), "primary", { anchor: "middle", size: 18, weight: 500 });
  markup += label(CX, CY + 20, observationCount.toLocaleString(), "muted", { anchor: "middle" });
  return { height: HEIGHT, markup };
};
