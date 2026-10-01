"use client";

import { Button } from "@/components/ui/button";
import { useId, useState, type FormEvent } from "react";

import { last24HoursRange, rangeToIso, todayRange, type DateRange } from "@/lib/dates";

interface DateRangeControlsProps {
  value: DateRange;
  onApply: (range: DateRange) => void;
  onCancel?: () => void;
}

export function DateRangeControls({ value, onApply, onCancel }: DateRangeControlsProps) {
  const errorId = useId();
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
      <div className="range-heading">
        <strong>自定义时间范围</strong>
        <p>按本地时间选择开始与结束</p>
      </div>
      <div className="range-shortcuts" aria-label="快捷时间范围">
        <Button variant="secondary" type="button" onClick={() => applyPreset(todayRange())}>
          今天
        </Button>
        <Button variant="secondary" type="button" onClick={() => applyPreset(last24HoursRange())}>
          最近 24 小时
        </Button>
      </div>
      <div className="range-fields">
        <label className="field">
          <span>开始时间</span>
          <input
            type="datetime-local"
            data-autofocus
            aria-invalid={Boolean(error)}
            aria-describedby={error ? errorId : undefined}
            value={draft.from}
            onChange={(event) => setDraft((current) => ({ ...current, from: event.target.value }))}
          />
        </label>
        <label className="field">
          <span>结束时间</span>
          <input
            type="datetime-local"
            aria-invalid={Boolean(error)}
            aria-describedby={error ? errorId : undefined}
            value={draft.to}
            onChange={(event) => setDraft((current) => ({ ...current, to: event.target.value }))}
          />
        </label>
      </div>
      {error ? (
        <p id={errorId} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
      <div className="range-actions">
        {onCancel ? (
          <Button variant="ghost" onClick={onCancel}>
            取消
          </Button>
        ) : null}
        <Button variant="default" type="submit">
          应用范围
        </Button>
      </div>
    </form>
  );
}
