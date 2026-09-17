import {
  useId,
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
import { densityBuckets, type DensityBucket } from "./densitySeries";
import {
  CURVE_HEIGHT,
  CURVE_WIDTH,
  densityGeometry,
  type DensityGeometry,
} from "./densityGeometry";
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
  click: (event: MouseEvent<SVGSVGElement>) => void;
  hover: (event: PointerEvent<SVGSVGElement>) => void;
  leave: () => void;
  keyDown: (event: KeyboardEvent<SVGSVGElement>) => void;
}

/**
 * Pointer and keyboard reading of the drillable buckets. Selection snaps to real bucket edges rather
 * than to the drawing grid, so opening a window always lands on records that exist.
 */
function useBucketSelection({
  choices,
  range,
  geometry,
  tooltip,
  onChoose,
}: {
  choices: DensityBucket[];
  range: TimeRange;
  geometry: DensityGeometry;
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
  function timeFrom(event: { clientX: number }, element: SVGSVGElement) {
    const rect = element.getBoundingClientRect();
    if (!rect.width) return null;
    return range.start + ((event.clientX - rect.left) / rect.width) * (range.end - range.start);
  }
  /** Arrow keys walk the bucket list; entering from no selection lands at the near end of the travel. */
  function step(event: KeyboardEvent<SVGSVGElement>) {
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
  function keyDown(event: KeyboardEvent<SVGSVGElement>) {
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
    const sample = at === null ? null : geometry.readingAt(at);
    if (!sample) return null;
    return <Reading from={sample.start} to={sample.end} value="没有记录" />;
  }
  function hover(event: PointerEvent<SVGSVGElement>) {
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

/** Invisible per-bucket targets, so the whole lane height is clickable rather than just the curve. */
function HitTargets({
  choices,
  geometry,
}: {
  choices: DensityBucket[];
  geometry: DensityGeometry;
}) {
  return (
    <>
      {choices.map((bucket) => (
        <rect
          key={bucket.start}
          className="density-hit"
          x={geometry.at(bucket.start)}
          y="0"
          width={Math.max(0.5, geometry.at(bucket.end) - geometry.at(bucket.start))}
          height={CURVE_HEIGHT}
        />
      ))}
    </>
  );
}

function HoverDot({ bucket, geometry }: { bucket: DensityBucket; geometry: DensityGeometry }) {
  const middle = (bucket.start + bucket.end) / 2;
  return (
    <circle
      className="density-hover-dot"
      cx={geometry.at(middle)}
      cy={geometry.heightAt(middle)}
      r="2.5"
    />
  );
}

function KeyboardRule({ bucket, geometry }: { bucket: DensityBucket; geometry: DensityGeometry }) {
  const x = geometry.at(bucket.start);
  return <line className="density-keyboard-marker" x1={x} x2={x} y1="0" y2={CURVE_HEIGHT} />;
}

function Markers({ selection, geometry }: { selection: Selection; geometry: DensityGeometry }) {
  const { marked, active } = selection;
  return (
    <>
      {marked ? <HoverDot bucket={marked} geometry={geometry} /> : null}
      {active ? <KeyboardRule bucket={active} geometry={geometry} /> : null}
    </>
  );
}

export function DensityCurve({ track, counts, detailCounts, range, scale, onSelect }: Props) {
  const gradient = useId();
  const tooltip = useTooltip();
  const series = scale.seriesFor(track.id);
  const geometry = useMemo(
    () => densityGeometry(series, range, scale.peak),
    [series, range, scale.peak],
  );
  const choices = useMemo(
    () => densityBuckets(counts, detailCounts, range, geometry.step / 1000),
    [counts, detailCounts, range, geometry],
  );
  const selection = useBucketSelection({
    choices,
    range,
    geometry,
    tooltip,
    onChoose: (bucket) =>
      onSelect({
        trackId: track.id,
        from: new Date(bucket.start).toISOString(),
        to: new Date(bucket.end).toISOString(),
        count: bucket.count,
      }),
  });
  if (!counts && !detailCounts.length) return null;

  const readout = selection.marked
    ? `；${formatTime(selection.marked.start, true)}，${selection.marked.count} 条`
    : "";
  return (
    <svg
      className="timeline-density-curve"
      viewBox={`0 0 ${CURVE_WIDTH} ${CURVE_HEIGHT}`}
      preserveAspectRatio="none"
      role="button"
      tabIndex={0}
      data-source-seconds={geometry.sourceSeconds}
      data-peak={Math.round(geometry.peak)}
      data-scale-peak={Math.round(geometry.scalePeak)}
      aria-label={`${trackLabel(track)}输入密度曲线，峰值 ${Math.round(geometry.scalePeak)} 条，方向键选择时间桶，回车查看记录${readout}`}
      onClick={selection.click}
      onPointerMove={selection.hover}
      onPointerLeave={selection.leave}
      onKeyDown={selection.keyDown}
    >
      <defs>
        <linearGradient id={gradient} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="var(--chart-2)" stopOpacity="0.55" />
          <stop offset="100%" stopColor="var(--chart-2)" stopOpacity="0.04" />
        </linearGradient>
      </defs>
      {geometry.segments.map((segment) => (
        <path
          key={segment.key}
          className="density-wave"
          d={segment.area}
          fill={`url(#${gradient})`}
        />
      ))}
      {geometry.segments.map((segment) => (
        <path key={`line-${segment.key}`} className="density-line" d={segment.line} />
      ))}
      <HitTargets choices={choices} geometry={geometry} />
      <Markers selection={selection} geometry={geometry} />
    </svg>
  );
}
