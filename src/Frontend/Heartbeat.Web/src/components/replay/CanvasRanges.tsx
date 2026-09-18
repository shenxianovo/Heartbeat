import { useCallback, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import { describeRecord } from "@/components/records/renderers/registry";
import { TooltipReading, useTooltip } from "@/components/ui/Tooltip";
import { rowGeometry, rowTop, type RangeItem } from "./rangeLayout";
import { rangeReading, type RangeMediumProps as Props } from "./rangeMedium";
import type { TimeRange } from "./timeRange";

function bar(item: RangeItem, range: TimeRange, width: number) {
  const scale = width / (range.end - range.start);
  return {
    x: (item.start - range.start) * scale,
    width: Math.max(rowGeometry.minWidth, (item.end - item.start) * scale),
    y: rowTop(item.row),
  };
}

/** Topmost bar under the pointer, or -1; later items draw over earlier ones. */
function hit(items: RangeItem[], range: TimeRange, width: number, x: number, y: number): number {
  for (let index = items.length - 1; index >= 0; index -= 1) {
    const shape = bar(items[index]!, range, width);
    if (
      x >= shape.x &&
      x <= shape.x + shape.width &&
      y >= shape.y &&
      y <= shape.y + rowGeometry.bar
    )
      return index;
  }
  return -1;
}

/** Tones read the same custom properties as the CSS bars, so density does not change colour. */
function palette(canvas: HTMLCanvasElement) {
  const style = getComputedStyle(canvas);
  const value = (name: string) => style.getPropertyValue(name).trim();
  return {
    tones: {
      default: { fill: value("--primary"), alpha: 0.8 },
      muted: { fill: value("--muted-foreground"), alpha: 0.4 },
      attention: { fill: value("--primary-soft"), alpha: 1 },
    },
    selected: value("--primary-dark"),
    cursor: value("--foreground"),
  };
}

function drawRanges(canvas: HTMLCanvasElement, props: Props, cursor: number | null) {
  const bounds = canvas.getBoundingClientRect();
  if (!bounds.width || !bounds.height) return;
  const context = canvas.getContext("2d");
  if (!context) return;
  const ratio = window.devicePixelRatio || 1;
  const width = Math.round(bounds.width * ratio);
  const height = Math.round(bounds.height * ratio);
  // Resizing the surface reallocates it, so only do that when the lane really changed size.
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.clearRect(0, 0, bounds.width, bounds.height);
  const colors = palette(canvas);
  props.items.forEach((item, index) => {
    const shape = bar(item, props.range, bounds.width);
    const tone = colors.tones[describeRecord(props.track, item.record).tone ?? "default"];
    const selected = item.record.id === props.selectedId;
    context.globalAlpha = selected ? 1 : tone.alpha;
    context.fillStyle = tone.fill;
    context.fillRect(shape.x, shape.y, shape.width, rowGeometry.bar);
    context.globalAlpha = 1;
    context.lineWidth = 1;
    if (selected) {
      context.setLineDash([]);
      context.strokeStyle = colors.selected;
      context.strokeRect(shape.x - 0.5, shape.y - 0.5, shape.width + 1, rowGeometry.bar + 1);
    }
    if (index === cursor) {
      context.setLineDash([3, 2]);
      context.strokeStyle = colors.cursor;
      context.strokeRect(shape.x - 1.5, shape.y - 1.5, shape.width + 3, rowGeometry.bar + 3);
      context.setLineDash([]);
    }
  });
}

/**
 * Dense lanes draw their bars on one surface instead of one DOM node per observation.
 * Selection, hover and keyboard reading stay on this element, and content still comes
 * from the protocol presentation registry.
 */
export function CanvasRanges(props: Props) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const tooltip = useTooltip();
  const [cursor, setCursor] = useState(0);
  const [focused, setFocused] = useState(false);
  const index = Math.min(cursor, props.items.length - 1);
  const current = props.items[index];

  const repaint = useCallback(() => {
    if (canvas.current) drawRanges(canvas.current, props, focused ? index : null);
  }, [props, focused, index]);
  const latest = useRef(repaint);
  useLayoutEffect(() => {
    latest.current = repaint;
    repaint();
  }, [repaint]);
  useLayoutEffect(() => {
    const surface = canvas.current;
    if (!surface || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => latest.current());
    observer.observe(surface);
    return () => observer.disconnect();
  }, []);

  function itemAt(event: { currentTarget: HTMLCanvasElement; clientX: number; clientY: number }) {
    const bounds = event.currentTarget.getBoundingClientRect();
    const at = hit(
      props.items,
      props.range,
      bounds.width,
      event.clientX - bounds.left,
      event.clientY - bounds.top,
    );
    return at < 0 ? null : props.items[at]!;
  }

  function move(step: number, event: KeyboardEvent) {
    event.preventDefault();
    setCursor(Math.max(0, Math.min(props.items.length - 1, index + step)));
  }

  return (
    <canvas
      ref={canvas}
      className="timeline-range-canvas"
      data-segments={props.items.length}
      data-range={`${props.range.start}/${props.range.end}`}
      role="button"
      tabIndex={0}
      aria-label={
        current
          ? `${props.items.length} 条区间；方向键浏览，回车查看；当前 ${rangeReading(props.track, current).value}`
          : `${props.items.length} 条区间`
      }
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      onPointerMove={(event) => {
        const item = itemAt(event);
        if (!item) return tooltip.hide();
        const text = rangeReading(props.track, item);
        tooltip.show(<TooltipReading caption={text.caption} value={text.value} />, event);
      }}
      onPointerLeave={tooltip.hide}
      onClick={(event) => {
        const item = itemAt(event);
        if (item) props.onSelect(item.record.id);
      }}
      onKeyDown={(event) => {
        if (event.key === "ArrowRight" || event.key === "ArrowDown") move(1, event);
        else if (event.key === "ArrowLeft" || event.key === "ArrowUp") move(-1, event);
        else if ((event.key === "Enter" || event.key === " ") && current) {
          event.preventDefault();
          props.onSelect(current.record.id);
        }
      }}
    />
  );
}
