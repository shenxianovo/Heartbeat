export interface CurvePoint {
  x: number;
  y: number;
}

/**
 * Fritsch–Carlson tangents. Plain Catmull-Rom overshoots around spikes, which on a density curve
 * would dip the line below the baseline between two empty samples and clip against the lane floor.
 * Monotone segments cannot overshoot, so a run of zeros stays flat on zero.
 */
function tangents(points: CurvePoint[]): number[] {
  const last = points.length - 1;
  const slopes = points.slice(0, last).map((point, index) => {
    const next = points[index + 1]!;
    const run = next.x - point.x;
    return run === 0 ? 0 : (next.y - point.y) / run;
  });
  return points.map((_, index) => {
    if (index === 0) return slopes[0] ?? 0;
    if (index === last) return slopes[last - 1] ?? 0;
    const before = slopes[index - 1]!;
    const after = slopes[index]!;
    if (before * after <= 0) return 0;
    const limit = 3 * Math.min(Math.abs(before), Math.abs(after));
    const tangent = (before + after) / 2;
    return Math.sign(tangent) * Math.min(Math.abs(tangent), limit);
  });
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

/** Renders one smooth open polyline through every point, as a single `M`-rooted subpath. */
export function smoothLine(points: CurvePoint[]): string {
  if (!points.length) return "";
  const head = points[0]!;
  if (points.length === 1) return `M ${round(head.x)} ${round(head.y)}`;
  const slopes = tangents(points);
  let path = `M ${round(head.x)} ${round(head.y)}`;
  for (let index = 0; index < points.length - 1; index++) {
    const from = points[index]!;
    const to = points[index + 1]!;
    const third = (to.x - from.x) / 3;
    path +=
      ` C ${round(from.x + third)} ${round(from.y + slopes[index]! * third)}` +
      ` ${round(to.x - third)} ${round(to.y - slopes[index + 1]! * third)}` +
      ` ${round(to.x)} ${round(to.y)}`;
  }
  return path;
}

/** Same curve as {@link smoothLine}, closed down to the baseline so it can be filled. */
export function smoothArea(points: CurvePoint[], baseline: number): string {
  if (points.length < 2) return "";
  const line = smoothLine(points);
  const first = points[0]!;
  const last = points[points.length - 1]!;
  return `${line} L ${round(last.x)} ${round(baseline)} L ${round(first.x)} ${round(baseline)} Z`;
}
