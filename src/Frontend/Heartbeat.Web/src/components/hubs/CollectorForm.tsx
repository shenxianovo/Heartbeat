"use client";

import { useState, type FormEvent } from "react";
import type { CollectorOperation, CollectorState, CollectorType } from "@/api/hubs";
import { Button } from "@/components/ui/Button";

export function CollectorForm({
  type,
  collector,
  busy,
  submit,
  close,
}: {
  type: CollectorType;
  collector?: CollectorState;
  busy: boolean;
  submit: (operation: CollectorOperation) => Promise<void>;
  close: () => void;
}) {
  const [target, setTarget] = useState(collector?.target ?? "");
  const [values, setValues] = useState<Record<string, string | number>>(
    collector?.configuration ?? {},
  );

  async function save(event: FormEvent) {
    event.preventDefault();
    const configuration = Object.fromEntries(
      Object.entries(values).filter(([, value]) => value !== ""),
    );
    try {
      await submit({ action: "configure", key: type.key, target, configuration });
    } finally {
      setValues((previous) =>
        Object.fromEntries(
          Object.entries(previous).filter(
            ([name]) =>
              !type.fields.some((field) => field.name === name && field.kind === "secret"),
          ),
        ),
      );
    }
  }

  return (
    <form
      className="collector-form"
      onSubmit={(event) => void save(event)}
      aria-label={`${type.displayName} 配置`}
    >
      <h3>
        {collector ? "配置" : "添加"} {type.displayName}
      </h3>
      <label>
        {type.targetLabel}
        <input
          required
          value={target}
          disabled={!!collector || busy}
          onChange={(event) => setTarget(event.target.value.trim())}
        />
      </label>
      {type.fields.map((field) => (
        <label key={field.name}>
          {field.label}
          <input
            type={
              field.kind === "secret" ? "password" : field.kind === "number" ? "number" : "text"
            }
            autoComplete="off"
            required={field.required}
            disabled={busy}
            value={values[field.name] ?? ""}
            onChange={(event) =>
              setValues({
                ...values,
                [field.name]:
                  field.kind === "number" && event.target.value !== ""
                    ? Number(event.target.value)
                    : event.target.value,
              })
            }
          />
        </label>
      ))}
      <div className="hub-actions">
        <Button type="submit" disabled={busy}>
          {busy ? "正在执行…" : "保存配置"}
        </Button>
        <Button onClick={close} disabled={busy}>
          关闭
        </Button>
      </div>
      <p>保存配置会暂停此项采集。完成认证后，点击“开始”采集。</p>
    </form>
  );
}
