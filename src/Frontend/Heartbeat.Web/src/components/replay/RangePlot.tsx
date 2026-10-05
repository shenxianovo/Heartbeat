import { useMemo, useState, type KeyboardEvent } from "react";
import { describeRecord } from "@/components/records/renderers/registry";
import { TooltipReading, useTooltip } from "@/components/ui/Tooltip";
import { rowGeometry, rowTop, type RangeItem } from "./rangeLayout";
import type { TrackSummary } from "@/api/types";
import { formatTime, type TimeRange } from "./timeRange";
import uPlot from "uplot";
import { plotColor, useTimePlot } from "./useTimePlot";

/** Geometry uses uPlot's scale and is shared by drawing, hit testing and focus. */
function bar(plot: uPlot, item: RangeItem) {
  const x = plot.valToPos(item.start, "x");
  return {
    x,
    width: Math.max(rowGeometry.minWidth, plot.valToPos(item.end, "x") - x),
    y: rowTop(item.row),
  };
}

function rangePaths(props: Props): uPlot.Series.PathBuilder {
  return (plot) => {
    const fills = new Map<string, Path2D>();
    const colors = {
      default: plotColor(plot, "--primary"),
      muted: `color-mix(in srgb, ${plotColor(plot, "--muted-foreground")} 40%, transparent)`,
      attention: plotColor(plot, "--primary-soft"),
    };
    const ratio = uPlot.pxRatio;
    for (const item of props.items) {
      const shape = bar(plot, item);
      const color = colors[describeRecord(props.track, item.record).tone ?? "default"];
      let path = fills.get(color);
      if (!path) {
        path = new Path2D();
        fills.set(color, path);
      }
      path.rect(shape.x * ratio, shape.y * ratio, shape.width * ratio, rowGeometry.bar * ratio);
    }
    return { fill: fills, stroke: null };
  };
}

function outline(plot: uPlot, item: RangeItem, keyboard: boolean) {
  const shape = bar(plot, item);
  const context = plot.ctx;
  context.save();
  context.scale(uPlot.pxRatio, uPlot.pxRatio);
  context.strokeStyle = plotColor(plot, keyboard ? "--foreground" : "--primary-dark");
  context.setLineDash(keyboard ? [3, 2] : []);
  context.lineWidth = 1;
  context.strokeRect(shape.x, shape.y, shape.width, rowGeometry.bar);
  context.restore();
}

/**
 * Every lane draws on one uPlot surface, including expanded application sublanes.
 * Selection, hover and keyboard reading stay on this element, and content still comes
 * from the protocol presentation registry.
 */
export function RangePlot(props: Props) {
  const tooltip = useTooltip();
  const [cursor, setCursor] = useState(0);
  const [focused, setFocused] = useState(false);
  const index = Math.min(cursor, props.items.length - 1);
  const current = props.items[index];

  const model = useMemo(
    () => ({
      data: [props.items.map((item) => item.start), props.items.map(() => 1)] as uPlot.AlignedData,
      range: props.range,
      paths: rangePaths(props),
      draw: (plot: uPlot) => {
        const selected = props.items.find((item) => item.record.id === props.selectedId);
        if (selected) outline(plot, selected, false);
        if (focused && current) outline(plot, current, true);
      },
    }),
    [props, focused, current],
  );
  const { host, chart } = useTimePlot(model);

  function itemAt(event: { currentTarget: HTMLDivElement; clientX: number; clientY: number }) {
    const plot = chart.current;
    if (!plot) return null;
    const bounds = event.currentTarget.getBoundingClientRect();
    const x = event.clientX - bounds.left;
    const y = event.clientY - bounds.top;
    return (
      props.items.findLast((item) => {
        const shape = bar(plot, item);
        return (
          x >= shape.x &&
          x <= shape.x + shape.width &&
          y >= shape.y &&
          y <= shape.y + rowGeometry.bar
        );
      }) ?? null
    );
  }

  function move(step: number, event: KeyboardEvent) {
    event.preventDefault();
    setCursor(Math.max(0, Math.min(props.items.length - 1, index + step)));
  }

  return (
    <div
      ref={host}
      className="timeline-range-plot"
      data-segments={props.items.length}
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

interface Props {
  track: TrackSummary;
  items: RangeItem[];
  range: TimeRange;
  selectedId: string | null;
  onSelect: (recordId: string) => void;
}

function rangeReading(track: TrackSummary, item: RangeItem) {
  const summary = describeRecord(track, item.record);
  return {
    caption: `${formatTime(item.start, true)} – ${formatTime(item.end, true)}`,
    value: summary.hover ?? summary.label,
  };
}
