import { Button } from "@/components/ui/button";
import { Popover } from "@/components/ui/Popover";
import { Icon } from "@/components/ui/Icon";
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
          <Button
            key={days}
            variant="glass"
            aria-pressed={
              range.from === recentRange(days).from && range.to === recentRange(days).to
            }
            onClick={() => onChange(recentRange(days))}
          >
            {days} 天
          </Button>
        ))}
      </div>
      <Popover
        label="时间范围"
        className="range-picker period-range-picker"
        trigger={
          <>
            <Icon name="calendar" />
            <span className="picker-value">
              {range.from.slice(0, 10).replaceAll("-", "/")} —{" "}
              {range.to.slice(0, 10).replaceAll("-", "/")}
            </span>
            <Icon name="chevronDown" className="range-chevron" />
          </>
        }
      >
        {(close) => (
          <DateRangeControls
            key={`${range.from}/${range.to}`}
            value={range}
            onCancel={close}
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
