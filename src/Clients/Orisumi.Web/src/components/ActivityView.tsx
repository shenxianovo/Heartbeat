import { useEffect, useRef, useState } from "react";
import type { ActivityStore } from "@/lib/activity";
import type { ViewKey } from "@/lib/preferences";
import { createBubbleRenderer } from "@/views/bubble";
import { WIDTH, type Renderer } from "@/views/common";
import { renderEcg } from "@/views/ecg";
import { renderRing } from "@/views/ring";
import { renderStrip } from "@/views/strip";

const TITLES: Record<ViewKey, string> = {
  ecg: "最近 20 秒的写入",
  bubble: "各 Observer 最近 10 分钟的写入量",
  strip: "各 Observer 最近 10 分钟的写入时刻",
  ring: "今天各 Observer 的 Observation，按事实时间",
};

function createRenderer(view: ViewKey): Renderer {
  switch (view) {
    case "ecg":
      return renderEcg;
    case "bubble":
      return createBubbleRenderer();
    case "strip":
      return renderStrip;
    case "ring":
      return renderRing;
  }
}

/**
 * 视图每帧都可能变化，所以按帧生成 SVG 标记写进 innerHTML，不经过 React 渲染。
 * 文字都经过转义。元素每帧都会替换，click 的按下和抬起可能落在不同的元素上，所以在 pointerdown 时按 data-key 判断点中了谁。
 */
export function ActivityView({
  view,
  store,
  dayStart,
  selected,
  reduced,
  onSelect,
}: {
  view: ViewKey;
  store: ActivityStore;
  dayStart: number;
  selected: string | null;
  reduced: boolean;
  onSelect: (key: string | null) => void;
}) {
  const svg = useRef<SVGSVGElement>(null);
  const [expanded, setExpanded] = useState(false);
  const state = useRef({ selected, expanded, dayStart, reduced });
  useEffect(() => {
    state.current = { selected, expanded, dayStart, reduced };
  }, [selected, expanded, dayStart, reduced]);

  useEffect(() => {
    const renderer = createRenderer(view);
    let frame = 0;
    let last = "";
    let lastAt = 0;
    const draw = () => {
      frame = requestAnimationFrame(draw);
      const now = Date.now();
      const { reduced: still, ...rest } = state.current;
      if (still && now - lastAt < 1000) return;
      lastAt = now;
      const element = svg.current;
      if (!element) return;
      const width = Math.round(element.clientWidth) || WIDTH;
      const snapshot = store.snapshot(now);
      const output = renderer({
        ...rest,
        width,
        reduced: still,
        now,
        observers: snapshot.observers,
        observationCount: snapshot.observationCount,
      });
      element.setAttribute("viewBox", `0 0 ${width} ${output.height}`);
      element.setAttribute("height", String(output.height));
      if (output.markup !== last) {
        element.innerHTML = output.markup;
        last = output.markup;
      }
    };
    draw();
    return () => cancelAnimationFrame(frame);
  }, [view, store]);

  return (
    <svg
      ref={svg}
      role="img"
      aria-label={TITLES[view]}
      data-view={view}
      viewBox={`0 0 ${WIDTH} 320`}
      className="block w-full select-none [&_[data-key]]:cursor-pointer [&_[data-more]]:cursor-pointer"
      onPointerDown={(event) => {
        if (event.button !== 0) return;
        const target = event.target as Element;
        const hit = target.closest("[data-key]");
        if (hit) {
          const key = hit.getAttribute("data-key");
          onSelect(key === selected ? null : key);
          return;
        }
        if (target.closest("[data-more]")) {
          setExpanded((value) => !value);
          return;
        }
        onSelect(null);
      }}
    />
  );
}
