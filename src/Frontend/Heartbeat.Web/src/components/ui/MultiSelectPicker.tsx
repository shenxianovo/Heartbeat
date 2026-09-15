"use client";

import { Button } from "./Button";
import { Icon } from "./Icon";
import { Popover } from "./Popover";

interface Props {
  label: string;
  allLabel?: string;
  options: { value: string; label: string }[];
  value: string[];
  onChange: (value: string[]) => void;
}

export function MultiSelectPicker({ label, allLabel = "全部", options, value, onChange }: Props) {
  const selected = options.filter((option) => value.includes(option.value));
  const caption =
    selected.length === options.length
      ? allLabel
      : selected.length === 1
        ? selected[0]!.label
        : `${label} · ${selected.length}`;
  return (
    <Popover
      label={label}
      className="multi-select-picker"
      trigger={
        <>
          <Icon name="filter" />
          <span className="picker-value">{caption}</span>
          <Icon name="chevronDown" />
        </>
      }
    >
      {(close) => (
        <>
          <div className="picker-heading">
            <span>{label}</span>
            <span>
              {selected.length} / {options.length}
            </span>
          </div>
          <div className="picker-options">
            {options.map((option) => (
              <label key={option.value} className="picker-option">
                <input
                  type="checkbox"
                  checked={value.includes(option.value)}
                  onChange={(event) =>
                    onChange(
                      event.target.checked
                        ? [...value, option.value]
                        : value.filter((id) => id !== option.value),
                    )
                  }
                />
                <span className="picker-check" aria-hidden="true">
                  <Icon name="check" />
                </span>
                <span>{option.label}</span>
              </label>
            ))}
          </div>
          <div className="picker-footer picker-actions">
            <Button variant="ghost" onClick={() => onChange(options.map((option) => option.value))}>
              全选
            </Button>
            <Button variant="ghost" onClick={() => onChange([])}>
              清空
            </Button>
            <Button variant="glassPrimary" onClick={close}>
              完成
            </Button>
          </div>
        </>
      )}
    </Popover>
  );
}
