"use client";

import { useState } from "react";
import {
  operateCollector,
  retireHub,
  type CollectorOperation,
  type CollectorState,
  type CollectorType,
  type HubSummary,
} from "@/api/hubs";
import { Button } from "@/components/ui/button";
import { CollectorForm } from "./CollectorForm";
import { CollectorRow } from "./CollectorRow";

export function HubCard({
  hub,
  token,
  stale,
  refresh,
}: {
  hub: HubSummary;
  token: string;
  stale: boolean;
  refresh: () => Promise<unknown>;
}) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [form, setForm] = useState<{ type: CollectorType; collector?: CollectorState } | null>(
    null,
  );
  const available = hub.online && !hub.retired && !stale;

  async function operate(operation: CollectorOperation) {
    setBusy(true);
    setMessage(null);
    try {
      await operateCollector(token, hub.id, operation);
      setMessage("操作已执行，请查看最新采集状态。");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "操作失败。");
    } finally {
      setBusy(false);
      await refresh();
    }
  }
  async function retire() {
    if (
      !window.confirm(
        `退役 ${hub.report.displayName}？它将无法再接入管理，需要使用新的 Hub 身份重新接入。`,
      )
    )
      return;
    setBusy(true);
    try {
      await retireHub(token, hub.id);
      await refresh();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "退役失败。");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="hub-card" aria-label={hub.report.displayName}>
      <HubDetails hub={hub} available={available} stale={stale} />
      <h3>Collector</h3>
      {hub.report.collectors.length === 0 && <p>尚未配置 Collector。</p>}
      {hub.report.collectors.map((collector) => (
        <CollectorRow
          key={`${collector.key}:${collector.target}`}
          collector={collector}
          type={hub.report.types.find((item) => item.key === collector.key)}
          disabled={busy || !available}
          operate={operate}
          edit={setForm}
        />
      ))}
      <HubActions
        hub={hub}
        busy={busy}
        available={available}
        stale={stale}
        add={(type) => setForm({ type })}
        retire={retire}
      />
      {message && <p role="status">{message}</p>}
      {form && (
        <CollectorForm
          key={`${form.type.key}:${form.collector?.target ?? "new"}`}
          type={form.type}
          collector={form.collector}
          busy={busy || !available}
          submit={operate}
          close={() => setForm(null)}
        />
      )}
    </section>
  );
}

function HubDetails({
  hub,
  available,
  stale,
}: {
  hub: HubSummary;
  available: boolean;
  stale: boolean;
}) {
  return (
    <>
      <div className="hub-heading">
        <div>
          <h2>{hub.report.displayName}</h2>
          <p>
            {hub.report.kind === "desktop" ? "Desktop" : "服务器 Hub"} · {hub.id}
          </p>
        </div>
        <span className={`hub-presence ${available ? "is-online" : ""}`}>
          {hub.retired ? "已退役" : stale ? "状态未知" : hub.online ? "在线" : "离线"}
        </span>
      </div>
      <p>最近联络：{new Date(hub.lastSeenAt).toLocaleString()}</p>
      {!available && <p>下方为最近上报的信息，当前无法执行采集操作。</p>}
      <div className="hub-delivery">
        <strong>Record 交付</strong>
        <span>待上传 {hub.report.delivery.pending}</span>
        <span>失败 {hub.report.delivery.failed}</span>
      </div>
      {hub.report.delivery.error && <p role="status">{hub.report.delivery.error}</p>}
    </>
  );
}

function HubActions({
  hub,
  busy,
  available,
  stale,
  add,
  retire,
}: {
  hub: HubSummary;
  busy: boolean;
  available: boolean;
  stale: boolean;
  add: (type: CollectorType) => void;
  retire: () => Promise<void>;
}) {
  return (
    <div className="hub-actions">
      {hub.report.types
        .filter((type) => type.canAdd)
        .map((type) => (
          <Button key={type.key} disabled={!available || busy} onClick={() => add(type)}>
            添加 {type.displayName}
          </Button>
        ))}
      {!hub.retired && (
        <Button disabled={busy || stale || hub.online} onClick={() => void retire()}>
          退役 Hub
        </Button>
      )}
    </div>
  );
}
