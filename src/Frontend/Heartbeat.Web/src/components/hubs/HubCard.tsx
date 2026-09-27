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
import { Icon } from "@/components/ui/Icon";
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
      <HubIdentity hub={hub} available={available} stale={stale} />
      <div className="hub-delivery">
        <h3>Record 交付</h3>
        <div className="hub-delivery-counts">
          <span>
            待上传 <strong>{hub.report.delivery.pending}</strong>
          </span>
          <span data-failed={hub.report.delivery.failed > 0}>
            失败 <strong>{hub.report.delivery.failed}</strong>
          </span>
        </div>
        {hub.report.delivery.error && (
          <p className="delivery-error" role="status">
            {hub.report.delivery.error}
          </p>
        )}
      </div>
      <DeliveryChart activity={available ? activity : undefined} />
      <div className="hub-collectors">
        <h3>
          Collector{" "}
          <span className="ml-1.5 font-normal text-muted-foreground">
            {hub.report.collectors.length}
          </span>
        </h3>
        {hub.report.collectors.length === 0 && <p>尚无 Collector 接入。</p>}
        {hub.report.collectors.map((collector) => (
          <CollectorRow
            key={`${collector.key}:${collector.target}`}
            collector={collector}
            type={hub.report.types.find((item) => item.key === collector.key)}
            disabled={!available || form !== null}
            stale={!available}
            login={setForm}
          />
        ))}
        <div className="flex flex-wrap items-center gap-2">
          {hub.report.types.map((type) => (
            <Button
              key={type.key}
              size="sm"
              className="max-w-full whitespace-normal"
              disabled={!available || form !== null}
              onClick={() => setForm({ type })}
            >
              {hub.report.collectors.some((collector) => collector.key === type.key)
                ? `添加其他 ${type.displayName} 账号`
                : `登录 ${type.displayName}`}
            </Button>
          ))}
        </div>
        {hub.report.kind === "desktop" && <p className="mt-1.5">在桌面客户端管理</p>}
      </div>
      {form && (
        <div className="hub-login-panel">
          <CollectorLogin
            key={`${form.type.key}:${form.target ?? "new"}`}
            type={form.type}
            target={form.target}
            disabled={!available}
            submit={login}
            close={() => setForm(null)}
          />
        </div>
      )}
    </section>
  );
}

function HubIdentity({
  hub,
  available,
  stale,
}: {
  hub: HubSummary;
  available: boolean;
  stale: boolean;
}) {
  return (
    <div className="flex items-start gap-3">
      <div className="hub-kind-icon">
        <Icon name={hub.report.kind === "desktop" ? "monitor" : "server"} size={28} />
      </div>
      <div className="min-w-0">
        <div className="hub-heading">
          <h2>{hub.report.displayName}</h2>
          <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1.5">
            <p>{hub.report.kind === "desktop" ? "Desktop" : "服务器 Hub"}</p>
            <span className="status-indicator" data-tone={available ? "success" : "muted"}>
              {hub.retired ? "已退役" : stale ? "状态未知" : hub.online ? "在线" : "离线"}
            </span>
          </div>
        </div>
        <p className="mt-2 tabular-nums">
          最近联络
          <br />
          <time dateTime={hub.lastSeenAt}>{new Date(hub.lastSeenAt).toLocaleString()}</time>
        </p>
        <details className="hub-identifiers">
          <summary>查看标识</summary>
          <p>{hub.id}</p>
        </details>
        {!available && <p className="mt-2">最近上报信息 · 当前无法登录</p>}
      </div>
    </div>
  );
}
