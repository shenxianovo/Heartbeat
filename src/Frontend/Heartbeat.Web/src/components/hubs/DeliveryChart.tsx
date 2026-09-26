"use client";

import { useLayoutEffect, useRef } from "react";
import type { DeliveryActivity } from "@/api/hubs";
import { mountDeliveryPlot } from "./deliveryPlot";

export function DeliveryChart({ activity }: { activity?: DeliveryActivity }) {
  const host = useRef<HTMLDivElement>(null);
  const plot = useRef<ReturnType<typeof mountDeliveryPlot> | null>(null);
  useLayoutEffect(() => {
    plot.current = mountDeliveryPlot(host.current!);
    return () => {
      plot.current?.destroy();
      plot.current = null;
    };
  }, []);
  useLayoutEffect(() => {
    plot.current?.update(activity);
  }, [activity]);
  const counts = activity?.buckets.reduce<[number, number, number]>(
    (sum, bucket) => [sum[0] + bucket.received, sum[1] + bucket.sent, sum[2] + bucket.confirmed],
    [0, 0, 0],
  );
  return (
    <section className="delivery-chart" aria-label="Hub 最近收发活动">
      <div className="delivery-chart-title">
        <strong>最近收发</strong>
        <span>{activity ? "最近 60 秒 · Records / 秒" : "活动状态暂不可用"}</span>
      </div>
      <div className="delivery-legend" aria-hidden="true">
        <span>接收</span>
        <span>发送</span>
        <span>确认</span>
      </div>
      <div
        ref={host}
        className="delivery-plot"
        role="img"
        aria-label="最近 60 秒收发曲线，横轴时间，纵轴每秒 Record 快照数"
      />
      <output className="sr-only" aria-label="收发窗口合计">
        {counts ? `接收 ${counts[0]}，发送 ${counts[1]}，确认 ${counts[2]}` : "暂无数据"}
      </output>
      <p className="delivery-caption">每秒快照数，含重试与续期；当前秒持续更新，发送不代表成功。</p>
    </section>
  );
}
