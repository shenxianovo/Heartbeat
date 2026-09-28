import { Button } from "@/components/ui/button";
import { Popover } from "@/components/ui/Popover";
import { DateRangeControls } from "./DateRangeControls";
import type { DateRange } from "@/lib/dates";
import { recentRange } from "@/lib/viewRange";

export function PeriodControls({
  range,
  onChange,
}: {
  range: DateRange;
  onChange: (range: DateRange) => void;
}) {
  return (
    <>
      <div className="period-presets">
        {[7, 30, 90].map((days) => (
          <Button key={days} variant="glass" onClick={() => onChange(recentRange(days))}>
            {days} 天
          </Button>
        ))}
      </div>
      <Popover
        label="时间范围"
        trigger={
          <span>
            {range.from.slice(0, 10)} → {range.to.slice(0, 10)}
          </span>
        }
      >
        {(close) => (
          <DateRangeControls
            key={`${range.from}/${range.to}`}
            value={range}
            onApply={(next) => {
              onChange(next);
              close();
            }}
          />
        )}
      </Popover>
    </>
  );
}
