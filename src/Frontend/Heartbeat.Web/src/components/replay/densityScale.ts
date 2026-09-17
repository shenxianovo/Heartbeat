import type { ReplayLane } from "@/api/types";
import { densitySeries, type DensitySeries } from "./densitySeries";
import type { TimeRange } from "./timeRange";

/**
 * The single vertical scale every visible input-density lane is drawn against.
 *
 * A track is one collector's protocol, so each device contributes its own density lane. Normalising
 * per lane would draw a quiet machine exactly as tall as a busy one, which makes the lanes look
 * comparable while they are not. Sharing one peak buys that comparison, and the price is that a lane
 * which is quiet in absolute terms now reads as nearly flat — that is the honest picture.
 */
export interface DensityScale {
  /** The busiest sample across all point lanes in view, in events per sample. */
  peak: number;
  /** The sample grid for one lane, or null when that lane has nothing to draw. */
  seriesFor: (trackId: string) => DensitySeries | null;
}

export function densityScale(lanes: ReplayLane[], range: TimeRange): DensityScale {
  const series = new Map<string, DensitySeries | null>();
  let peak = 0;
  for (const lane of lanes) {
    if (lane.track.timeMode !== "point") continue;
    const value = densitySeries(lane.counts ?? null, lane.detailCounts ?? [], range);
    series.set(lane.track.id, value);
    peak = Math.max(peak, value?.peak ?? 0);
  }
  return { peak, seriesFor: (trackId: string) => series.get(trackId) ?? null };
}
