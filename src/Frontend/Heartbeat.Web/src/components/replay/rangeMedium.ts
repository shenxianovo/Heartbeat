import type { TrackSummary } from "@/api/types";
import { describeRecord } from "@/components/records/renderers/registry";
import type { RangeItem } from "./rangeLayout";
import { formatTime, type TimeRange } from "./timeRange";

/**
 * Both drawing media take the same input: the timeline owns time geometry and
 * selection, the protocol presentation registry owns the content.
 */
export interface RangeMediumProps {
  track: TrackSummary;
  items: RangeItem[];
  range: TimeRange;
  selectedId: string | null;
  onSelect: (recordId: string) => void;
}

/** What a bar reads out on hover, identical in either medium. */
export function rangeReading(track: TrackSummary, item: RangeItem) {
  const summary = describeRecord(track, item.record);
  return {
    caption: `${formatTime(item.start, true)} – ${formatTime(item.end, true)}`,
    value: summary.hover ?? summary.label,
  };
}
