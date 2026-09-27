"use client";

import { useState } from "react";
import {
  loginCollector,
  type CollectorLoginRequest,
  type CollectorType,
  type HubSummary,
  type DeliveryActivity,
} from "@/api/hubs";
import { Button } from "@/components/ui/button";
import { CollectorLogin } from "./CollectorLogin";
import { CollectorRow } from "./CollectorRow";
import { DeliveryChart } from "./DeliveryChart";

export function HubCard({
  hub,
  token,
  stale,
  activity,
  refresh,
}: {
  hub: HubSummary;
  token: string;
  stale: boolean;
  activity?: DeliveryActivity;
  refresh: () => Promise<unknown>;
}) {
  const [form, setForm] = useState<{ type: CollectorType; target?: string } | null>(null);
  const available = hub.online && !hub.retired && !stale;

  async function login(request: CollectorLoginRequest) {
    try {
      return await loginCollector(token, hub.id, request);
    } finally {
      await refresh();
    }
  }

  return (
    <section className="hub-card" aria-label={hub.report.displayName}>
      <HubDetails hub={hub} available={available} stale={stale} />
      <DeliveryChart activity={available ? activity : undefined} />
      <h3>Collector</h3>
      {hub.report.collectors.length === 0 && <p>尚无 Collector 接入。</p>}
      {hub.report.collectors.map((collector) => (
        <CollectorRow
          key={`${collector.key}:${collector.target}`}
          collector={collector}
          type={hub.report.types.find((item) => item.key === collector.key)}
          disabled={!available || form !== null}
          login={setForm}
        />
      ))}
      <div className="hub-actions">
        {hub.report.types.map((type) => (
          <Button
            key={type.key}
            disabled={!available || form !== null}
            onClick={() => setForm({ type })}
          >
            登录 {type.displayName}
          </Button>
        ))}
      </div>
      {form && (
        <CollectorLogin
          key={`${form.type.key}:${form.target ?? "new"}`}
          type={form.type}
          target={form.target}
          disabled={!available}
          submit={login}
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
      {!available && <p>下方为最近上报的信息，当前无法登录 Collector。</p>}
      <div className="hub-delivery">
        <strong>Record 交付</strong>
        <span>待上传 {hub.report.delivery.pending}</span>
        <span>失败 {hub.report.delivery.failed}</span>
      </div>
      {hub.report.delivery.error && <p role="status">{hub.report.delivery.error}</p>}
    </>
  );
}
