import { TooltipReading, useTooltip } from "@/components/ui/Tooltip";
import { describeRecord } from "@/components/records/renderers/registry";
import { rowGeometry, rowTop } from "./rangeLayout";
import { rangeReading, type RangeMediumProps } from "./rangeMedium";
import { percent } from "./timeRange";

/**
 * Sparse lanes keep one node per observation, so each bar stays focusable and
 * styled by CSS. Hover and selection are delegated to the group: per-bar
 * handlers would be rebuilt on every frame of a drag.
 */
export function RangeBars(props: RangeMediumProps) {
  const tooltip = useTooltip();
  const itemAt = (target: EventTarget) => {
    const index = (target as Element).closest<HTMLElement>(".timeline-range")?.dataset.index;
    return index === undefined ? null : (props.items[Number(index)] ?? null);
  };
  return (
    <div
      className="timeline-range-bars"
      onPointerMove={(event) => {
        const item = itemAt(event.target);
        if (!item) return tooltip.hide();
        const text = rangeReading(props.track, item);
        tooltip.show(<TooltipReading caption={text.caption} value={text.value} />, event);
      }}
      onPointerLeave={tooltip.hide}
      onClick={(event) => {
        const item = itemAt(event.target);
        if (item) props.onSelect(item.record.id);
      }}
    >
      {props.items.map((item, index) => {
        const summary = describeRecord(props.track, item.record);
        return (
          <button
            key={item.record.id}
            type="button"
            className={`timeline-range timeline-tone-${summary.tone ?? "default"}`}
            data-index={index}
            style={{
              left: `${percent(item.start, props.range)}%`,
              width: `${((item.end - item.start) / (props.range.end - props.range.start)) * 100}%`,
              top: `${rowTop(item.row)}px`,
              minWidth: `${rowGeometry.minWidth}px`,
              height: `${rowGeometry.bar}px`,
            }}
            aria-label={summary.hover ?? summary.label}
            aria-pressed={item.record.id === props.selectedId}
          />
        );
      })}
    </div>
  );
}
