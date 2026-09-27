"use client";

import { useState, type FormEvent } from "react";
import type { CollectorLoginRequest, CollectorLoginResult, CollectorType } from "@/api/hubs";
import { Button } from "@/components/ui/button";

export function CollectorLogin({
  type,
  target,
  disabled,
  submit,
  close,
}: {
  type: CollectorType;
  target?: string;
  disabled: boolean;
  submit: (request: CollectorLoginRequest) => Promise<CollectorLoginResult>;
  close: () => void;
}) {
  const [fields, setFields] = useState(type.fields);
  const [sessionId, setSessionId] = useState<string>();
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function login(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const input = values;
    setValues({});
    try {
      const result = await submit({ key: type.key, target, sessionId, input });
      if (result.target) {
        close();
        return;
      }
      setSessionId(result.sessionId ?? undefined);
      setFields(result.fields);
      setError(result.error);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "登录失败。");
      setSessionId(undefined);
      setFields(type.fields);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="collector-form"
      onSubmit={(event) => void login(event)}
      aria-label={`${type.displayName} 登录`}
    >
      <h3>登录 {type.displayName}</h3>
      {fields.map((field) => (
        <label key={field.name}>
          {field.label}
          <input
            type={field.kind === "secret" ? "password" : "text"}
            autoComplete="off"
            required={field.required}
            disabled={disabled || busy}
            value={values[field.name] ?? ""}
            onChange={(event) => setValues({ ...values, [field.name]: event.target.value })}
          />
        </label>
      ))}
      {error && <p role="alert">{error}</p>}
      <div className="hub-actions">
        <Button type="submit" disabled={disabled || busy}>
          {busy ? "正在登录…" : sessionId ? "继续登录" : "登录"}
        </Button>
        <Button onClick={close} disabled={busy}>
          关闭
        </Button>
      </div>
    </form>
  );
}
