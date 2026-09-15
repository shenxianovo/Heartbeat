"use client";

import { Button } from "@/components/ui/Button";
import { useState, type FormEvent } from "react";

import { last24HoursRange, rangeToIso, todayRange, type DateRange } from "@/lib/dates";

interface DateRangeControlsProps {
  value: DateRange;
  onApply: (range: DateRange) => void;
}

export function DateRangeControls({ value, onApply }: DateRangeControlsProps) {
  const [draft, setDraft] = useState(value);
  const [error, setError] = useState<string | null>(null);

  function apply(event: FormEvent) {
    event.preventDefault();
    if (!rangeToIso(draft)) {
      setError("请选择有效的起止时间，结束时间需要晚于开始时间。");
      return;
    }
    setError(null);
    onApply(draft);
  }

  function applyPreset(next: DateRange) {
    setDraft(next);
    setError(null);
    onApply(next);
  }

  return (
    <form className="range-form" onSubmit={apply}>
      <div className="range-fields">
        <label className="field">
          <span>开始时间</span>
          <input
            type="datetime-local"
            value={draft.from}
            onChange={(event) => setDraft((current) => ({ ...current, from: event.target.value }))}
          />
        </label>
        <span className="range-arrow" aria-hidden="true">
          →
        </span>
        <label className="field">
          <span>结束时间</span>
          <input
            type="datetime-local"
            value={draft.to}
            onChange={(event) => setDraft((current) => ({ ...current, to: event.target.value }))}
          />
        </label>
      </div>
      <div className="range-actions">
        <Button variant="ghost" type="button" onClick={() => applyPreset(todayRange())}>
          今天
        </Button>
        <Button variant="ghost" type="button" onClick={() => applyPreset(last24HoursRange())}>
          最近 24 小时
        </Button>
        <Button variant="outline" type="submit">
          应用范围
        </Button>
      </div>
      {error ? (
        <p className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </form>
  );
}
