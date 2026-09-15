"use client";

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Button } from "./Button";
import { Icon } from "./Icon";
import { Popover } from "./Popover";

function dateKey(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}
function localDate(value: string) {
  return new Date(`${value}T12:00:00`);
}
interface Props {
  value: string;
  onChange: (value: string) => void;
  label?: string;
  maxDate?: string;
}

function Calendar({ value, onChange, maxDate }: Props) {
  const [month, setMonth] = useState(value.slice(0, 7));
  const [focused, setFocused] = useState(value);
  const grid = useRef<HTMLDivElement>(null);
  const today = dateKey(new Date());
  const first = localDate(`${month}-01`);
  const start = new Date(first);
  start.setDate(1 - ((first.getDay() + 6) % 7));
  const dates = Array.from({ length: 42 }, (_, index) => {
    const date = new Date(start);
    date.setDate(start.getDate() + index);
    return dateKey(date);
  });
  function moveMonth(delta: number) {
    const next = new Date(first);
    next.setMonth(first.getMonth() + delta);
    setMonth(dateKey(next).slice(0, 7));
  }
  useEffect(() => {
    grid.current
      ?.querySelector<HTMLButtonElement>(`[data-date="${focused}"]`)
      ?.focus({ preventScroll: true });
  }, [focused]);
  function key(event: KeyboardEvent<HTMLButtonElement>, day: string) {
    if (
      ![
        "ArrowLeft",
        "ArrowRight",
        "ArrowUp",
        "ArrowDown",
        "Home",
        "End",
        "PageUp",
        "PageDown",
      ].includes(event.key)
    )
      return;
    event.preventDefault();
    const date = localDate(day);
    if (event.key === "PageUp" || event.key === "PageDown") {
      const dayOfMonth = date.getDate();
      date.setDate(1);
      date.setMonth(date.getMonth() + (event.key === "PageUp" ? -1 : 1));
      const last = new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
      date.setDate(Math.min(dayOfMonth, last));
    } else {
      const weekday = (date.getDay() + 6) % 7;
      const delta =
        event.key === "Home"
          ? -weekday
          : event.key === "End"
            ? 6 - weekday
            : (
                { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 } as Record<
                  string,
                  number
                >
              )[event.key]!;
      date.setDate(date.getDate() + delta);
    }
    const next = dateKey(date);
    if (maxDate && next > maxDate) return;
    setMonth(next.slice(0, 7));
    setFocused(next);
  }
  const focusDay = dates.includes(focused) ? focused : `${month}-01`;
  return (
    <>
      <div className="calendar-panel">
        <div className="calendar-heading">
          <Button variant="outline" size="icon" aria-label="上个月" onClick={() => moveMonth(-1)}>
            <Icon name="chevronLeft" />
          </Button>
          <strong aria-live="polite">
            {first.getFullYear()}年 {first.getMonth() + 1}月
          </strong>
          <Button variant="outline" size="icon" aria-label="下个月" onClick={() => moveMonth(1)}>
            <Icon name="chevronRight" />
          </Button>
        </div>
        <div className="calendar-weekdays" aria-hidden="true">
          {"一二三四五六日".split("").map((day) => (
            <span key={day}>{day}</span>
          ))}
        </div>
        <div ref={grid} className="calendar-grid" role="grid" aria-label={`${month} 日历`}>
          {Array.from({ length: 6 }, (_, row) => (
            <div role="row" key={row}>
              {dates.slice(row * 7, row * 7 + 7).map((day) => (
                <div role="gridcell" key={day} aria-selected={day === value}>
                  <button
                    type="button"
                    className={`calendar-day${day.slice(0, 7) !== month ? " outside-month" : ""}`}
                    data-date={day}
                    data-autofocus={day === focusDay ? "" : undefined}
                    tabIndex={day === focusDay ? 0 : -1}
                    aria-label={day}
                    aria-current={day === today ? "date" : undefined}
                    aria-pressed={day === value}
                    disabled={Boolean(maxDate && day > maxDate)}
                    onKeyDown={(event) => key(event, day)}
                    onClick={() => onChange(day)}
                  >
                    {Number(day.slice(-2))}
                  </button>
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
      <div className="picker-footer">
        <Button
          variant="ghost"
          className="calendar-today"
          disabled={Boolean(maxDate && today > maxDate)}
          onClick={() => onChange(today)}
        >
          今天
        </Button>
      </div>
    </>
  );
}

export function DatePicker({ value, onChange, label = "选择日期", maxDate }: Props) {
  return (
    <Popover
      label={label}
      className="date-picker"
      trigger={
        <>
          <Icon name="calendar" />
          <span className="picker-value">{value}</span>
        </>
      }
    >
      {(close) => (
        <Calendar
          value={value}
          maxDate={maxDate}
          onChange={(next) => {
            onChange(next);
            close();
          }}
        />
      )}
    </Popover>
  );
}
