import { useMemo, useState } from "react";
import { shortId } from "@/lib/labels";
import type { ObservationItem } from "@/lib/observations";
import { formatDateTime } from "@/lib/time";

interface Props {
  observations: ObservationItem[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

const ROW_H = 44;

/**
 * Pulse 时间视图：把已加载的 Observation 投到一条时间轴。
 * - startAt=endAt：点（脉冲圆点）
 * - startAt<endAt：条段（左闭右开）
 * - endAt=null：向右渐隐的进行中段
 * 时间未知的观测单列在底部，不补白。
 */
export function PulseTimeline({ observations, selectedId, onSelect }: Props) {
  // “现在”只在挂载时取一次，避免渲染期调用不纯函数。
  const [nowMs] = useState(() => Date.now());

  const { timed, unknownTime } = useMemo(() => {
    const timed: ObservationItem[] = [];
    const unknownTime: ObservationItem[] = [];
    for (const o of observations) {
      if (o.startAt) timed.push(o);
      else unknownTime.push(o);
    }
    timed.sort((a, b) => (a.startAt! < b.startAt! ? -1 : 1));
    return { timed, unknownTime };
  }, [observations]);

  const { min, max, lanes } = useMemo(() => {
    if (timed.length === 0) return { min: 0, max: 0, lanes: [] as ObservationItem[][] };
    const starts = timed.map((o) => new Date(o.startAt!).getTime());
    const ends = timed.map((o) => (o.endAt ? new Date(o.endAt).getTime() : new Date(o.startAt!).getTime()));
    const min = Math.min(...starts);
    let max = Math.max(...ends);
    // endAt=null 的进行中段给一个 8% 的可视延伸。
    if (timed.some((o) => o.endAt == null)) max = min + (max - min || 60_000) * 1.08;
    const span = max - min || 1;

    // 简单泳道分配：贪心找第一个不重叠的泳道。
    const placed = timed.map((o) => {
      const s = new Date(o.startAt!).getTime();
      const e = o.endAt ? new Date(o.endAt).getTime() : max;
      return { o, s, e, point: o.endAt != null && s === new Date(o.endAt).getTime() };
    });
    const laneEnds: number[] = [];
    const lanesArr: ObservationItem[][] = [];
    for (const item of placed) {
      let lane = laneEnds.findIndex((end) => item.s >= end);
      if (lane === -1) {
        lane = laneEnds.length;
        laneEnds.push(0);
        lanesArr.push([]);
      }
      laneEnds[lane] = item.e;
      lanesArr[lane]!.push(item.o);
    }
    return { min, max, span, lanes: lanesArr };
  }, [timed]);

  if (observations.length === 0) {
    return (
      <div className="text-muted-foreground rounded-xl border border-dashed p-10 text-center text-sm">
        当前关系图里还没有观测（Observation）。展开更多引用后，事实时间会在这里汇聚。
      </div>
    );
  }

  const x = (t: number) => ((t - min) / (max - min || 1)) * 100;
  const height = Math.max(120, lanes.length * ROW_H + 48);

  return (
    <div className="bg-card/40 rounded-xl border p-4">
      <div className="text-muted-foreground mb-2 flex items-center justify-between text-xs">
        <span className="tabular">{formatDateTime(new Date(min).toISOString())}</span>
        <span className="tabular">{formatDateTime(new Date(max).toISOString())}</span>
      </div>

      <div className="relative w-full overflow-x-auto" style={{ minHeight: height }}>
        <div className="relative w-full" style={{ height }}>
          {/* 纵向时间网格 */}
          {[0.25, 0.5, 0.75].map((p) => (
            <div key={p} className="bg-border absolute top-0 bottom-8 w-px" style={{ left: `${p * 100}%` }} />
          ))}
          {/* 现在线 */}
          {nowMs > min && nowMs < max && (
            <div
              className="border-ongoing/60 absolute top-0 bottom-8 w-px border-l border-dashed"
              style={{ left: `${x(nowMs)}%` }}
            >
              <span className="bg-ongoing absolute -top-0.5 -translate-x-1/2 rounded-full px-1.5 py-0.5 text-[9px] text-white">
                now
              </span>
            </div>
          )}

          {lanes.flatMap((lane, laneIdx) =>
            lane.map((o) => {
              const s = new Date(o.startAt!).getTime();
              const isPoint = o.endAt != null && s === new Date(o.endAt).getTime();
              const ongoing = o.endAt == null;
              const e = ongoing ? max : new Date(o.endAt!).getTime();
              const left = x(s);
              const width = Math.max(0.6, x(e) - left);
              const top = 8 + laneIdx * ROW_H;
              const active = o.id === selectedId;
              return (
                <button
                  key={o.id}
                  type="button"
                  onClick={() => onSelect(o.id)}
                  className="absolute flex h-8 items-center rounded-lg text-left transition-all hover:brightness-110"
                  style={{
                    left: `${left}%`,
                    width: isPoint ? undefined : `${width}%`,
                    top,
                    minWidth: isPoint ? 16 : undefined,
                  }}
                  title={`${shortId(o.id)} · ${formatDateTime(o.startAt)}${o.endAt ? ` → ${formatDateTime(o.endAt)}` : " → 进行中"}`}
                  aria-label={`观测 ${shortId(o.id)}`}
                >
                  {isPoint ? (
                    <span
                      className={`border-cat-observation bg-card size-4 rounded-full border-2 ${active ? "ring-cat-observation/40 scale-125 ring-2" : ""}`}
                    />
                  ) : (
                    <span
                      className={`border-cat-observation/40 h-7 w-full rounded-lg border ${active ? "ring-cat-observation/50 ring-2" : ""} ${
                        ongoing
                          ? "from-cat-observation/55 to-cat-observation/5 bg-gradient-to-r"
                          : "bg-cat-observation/35"
                      }`}
                    />
                  )}
                </button>
              );
            }),
          )}
        </div>
      </div>

      <div className="text-muted-foreground mt-3 flex flex-wrap gap-4 text-[11px]">
        <span className="flex items-center gap-1.5">
          <span className="border-cat-observation bg-card size-3 rounded-full border-2" /> 时间点
        </span>
        <span className="flex items-center gap-1.5">
          <span className="bg-cat-observation/35 h-3 w-6 rounded" /> 区间（左闭右开）
        </span>
        <span className="flex items-center gap-1.5">
          <span className="from-cat-observation/55 to-cat-observation/5 h-3 w-6 rounded bg-gradient-to-r" /> 进行中 ·
          结束未知
        </span>
      </div>

      {unknownTime.length > 0 && (
        <div className="mt-4 border-t pt-3">
          <p className="eyebrow mb-2">事实时间未知（{unknownTime.length}）</p>
          <div className="flex flex-wrap gap-2">
            {unknownTime.map((o) => (
              <button
                key={o.id}
                type="button"
                onClick={() => onSelect(o.id)}
                className="text-muted-foreground hover:border-foreground/30 rounded-full border border-dashed px-3 py-1 font-mono text-xs"
              >
                {shortId(o.id)}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
