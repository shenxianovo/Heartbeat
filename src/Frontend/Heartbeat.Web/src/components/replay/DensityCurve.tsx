import {
  useMemo,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type PointerEvent,
  type ReactNode,
} from "react";
import type { PointCountsResponse, TrackSummary } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { TooltipReading, useTooltip, type TooltipHandle } from "@/components/ui/Tooltip";
import { densityBuckets, densityHeight, type DensityBucket } from "./densitySeries";
import uPlot from "uplot";
import { densityPlotData } from "./densityPlot";
import { plotColor, useTimePlot } from "./useTimePlot";
import type { DensitySeries } from "./densitySeries";
import type { DensityScale } from "./densityScale";
import { formatTime, type TimeRange } from "./timeRange";
import type { PointSelection } from "./TimelineLane";

interface Props {
  track: TrackSummary;
  counts: PointCountsResponse | null;
  detailCounts: PointCountsResponse[];
  range: TimeRange;
  /** Shared with the other point lanes so equal heights mean equal counts. */
  scale: DensityScale;
  onSelect: (value: PointSelection) => void;
}

interface Selection {
  /** The bucket the keyboard cursor rests on, marked with a rule. */
  active: DensityBucket | null;
  /** The bucket being read right now, whether by keyboard or pointer. */
  marked: DensityBucket | null;
  click: (event: MouseEvent<HTMLDivElement>) => void;
  hover: (event: PointerEvent<HTMLDivElement>) => void;
  leave: () => void;
  keyDown: (event: KeyboardEvent<HTMLDivElement>) => void;
}

/**
 * Pointer and keyboard reading of the drillable buckets. Selection snaps to real bucket edges rather
 * than to the drawing grid, so opening a window always lands on records that exist.
 */
function useBucketSelection({
  choices,
  range,
  series,
  tooltip,
  onChoose,
}: {
  choices: DensityBucket[];
  range: TimeRange;
  series: DensitySeries | null;
  tooltip: TooltipHandle;
  onChoose: (bucket: DensityBucket) => void;
}): Selection {
  const [keyboardAt, setKeyboardAt] = useState<number | null>(null);
  const [hoverAt, setHoverAt] = useState<number | null>(null);
  const bucketAt = (value: number | null) =>
    choices.find((bucket) => value !== null && value >= bucket.start && value < bucket.end) ?? null;
  const active = choices.find((bucket) => bucket.start === keyboardAt) ?? null;

  function choose(bucket: DensityBucket) {
    setKeyboardAt(bucket.start);
    onChoose(bucket);
  }
  function timeFrom(event: { clientX: number }, element: HTMLDivElement) {
    const rect = element.getBoundingClientRect();
    if (!rect.width) return null;
    return range.start + ((event.clientX - rect.left) / rect.width) * (range.end - range.start);
  }
  /** Arrow keys walk the bucket list; entering from no selection lands at the near end of the travel. */
  function step(event: KeyboardEvent<HTMLDivElement>) {
    event.preventDefault();
    const index = choices.findIndex((bucket) => bucket.start === keyboardAt);
    const last = choices.length - 1;
    const next =
      event.key === "ArrowRight"
        ? Math.min(last, index + 1)
        : index < 0
          ? last
          : Math.max(0, index - 1);
    setKeyboardAt(choices[next]!.start);
  }
  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (!choices.length) return;
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") step(event);
    else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      choose(active ?? choices[0]!);
    }
  }
  /**
   * The tip reports what is drawn under the pointer, which is not always something to open: a bucket
   * gives its real count, an observed-but-empty stretch says so, and an unobserved gap says nothing.
   */
  function reading(at: number | null): ReactNode {
    const bucket = bucketAt(at);
    if (bucket) return <Reading from={bucket.start} to={bucket.end} value={`${bucket.count} 条`} />;
    const sample =
      at === null
        ? null
        : series?.samples.find((sample) => sample.covered && at >= sample.start && at < sample.end);
    if (!sample) return null;
    return <Reading from={sample.start} to={sample.end} value="没有记录" />;
  }
  function hover(event: PointerEvent<HTMLDivElement>) {
    const at = timeFrom(event, event.currentTarget);
    setHoverAt(at);
    const content = reading(at);
    if (content) tooltip.show(content, event);
    else tooltip.hide();
  }
  return {
    active,
    marked: active ?? bucketAt(hoverAt),
    click: (event) => {
      const bucket = bucketAt(timeFrom(event, event.currentTarget));
      if (bucket) choose(bucket);
    },
    hover,
    leave: () => {
      setHoverAt(null);
      tooltip.hide();
    },
    keyDown,
  };
}

function Reading({ from, to, value }: { from: number; to: number; value: string }) {
  return (
    <TooltipReading caption={`${formatTime(from, true)} – ${formatTime(to, true)}`} value={value} />
  );
}

export function DensityCurve({ track, counts, detailCounts, range, scale, onSelect }: Props) {
  const tooltip = useTooltip();
  const series = scale.seriesFor(track.id);
  const choices = useMemo(
    () => densityBuckets(counts, detailCounts, range, (series?.step ?? 0) / 1000),
    [counts, detailCounts, range, series],
  );
  const selection = useBucketSelection({
    choices,
    range,
    series,
    tooltip,
    onChoose: (bucket) =>
      onSelect({
        trackId: track.id,
        from: new Date(bucket.start).toISOString(),
        to: new Date(bucket.end).toISOString(),
        count: bucket.count,
      }),
  });
  const { active, marked } = selection;
  const model = useMemo(
    () => ({
      data: densityPlotData(series, scale.peak),
      range,
      paths: uPlot.paths.spline!(),
      stroke: (plot: uPlot) => plotColor(plot, "--chart-2"),
      draw: (plot: uPlot) => drawDensityMarker(plot, series, scale.peak, active, marked),
      fill: (plot: uPlot) => {
        const color = plotColor(plot, "--chart-2");
        const gradient = plot.ctx.createLinearGradient(0, 0, 0, plot.bbox.height);
        gradient.addColorStop(0, `color-mix(in srgb, ${color} 55%, transparent)`);
        gradient.addColorStop(1, `color-mix(in srgb, ${color} 4%, transparent)`);
        return gradient;
      },
    }),
    [series, scale.peak, range, active, marked],
  );
  const { host } = useTimePlot(model);
  const readout = selection.marked
    ? `；${formatTime(selection.marked.start, true)}，${selection.marked.count} 条`
    : "";
  return (
    <div
      className="timeline-density-curve"
      ref={host}
      role="button"
      tabIndex={0}
      data-buckets={choices.length}
      data-keyboard-at={selection.active?.start}
      data-source-seconds={series?.sourceSeconds ?? 0}
      data-peak={Math.round(series?.peak ?? 0)}
      data-scale-peak={Math.round(scale.peak)}
      aria-label={`${trackLabel(track)}输入密度曲线，峰值 ${Math.round(scale.peak)} 条，方向键选择时间桶，回车查看记录${readout}`}
      onClick={selection.click}
      onPointerMove={selection.hover}
      onPointerLeave={selection.leave}
      onKeyDown={selection.keyDown}
    />
  );
}

function drawDensityMarker(
  plot: uPlot,
  series: DensitySeries | null,
  peak: number,
  active: DensityBucket | null,
  marked: DensityBucket | null,
) {
  const context = plot.ctx;
  context.save();
  context.scale(uPlot.pxRatio, uPlot.pxRatio);
  context.lineWidth = 1;
  if (active) {
    const x = plot.valToPos(active.start, "x");
    context.strokeStyle = plotColor(plot, "--foreground");
    context.beginPath();
    context.moveTo(x, 0);
    context.lineTo(x, plot.height);
    context.stroke();
  }
  if (marked) {
    const at = (marked.start + marked.end) / 2;
    const sample = series?.samples.find((sample) => at >= sample.start && at < sample.end);
    const y = densityHeight(sample?.count ?? 0, peak);
    context.beginPath();
    context.arc(plot.valToPos(at, "x"), plot.valToPos(y, "y"), 2.5, 0, 2 * Math.PI);
    context.fillStyle = plotColor(plot, "--background");
    context.strokeStyle = plotColor(plot, "--chart-2");
    context.fill();
    context.stroke();
  }
  context.restore();
}
