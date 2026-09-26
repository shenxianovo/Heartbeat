"use client";

import { useLayoutEffect, useRef } from "react";
import type { DeliveryActivity } from "@/api/hubs";
import { mountDeliveryPlot } from "./deliveryPlot";
import { deliveryStages } from "./deliverySamples";

export function DeliveryChart({ activity }: { activity?: DeliveryActivity }) {
  const summary = useRef<HTMLOutputElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const plot = useRef<ReturnType<typeof mountDeliveryPlot> | null>(null);
  useLayoutEffect(() => {
    plot.current = mountDeliveryPlot(host.current!, summary.current!);
    return () => {
      plot.current?.destroy();
      plot.current = null;
    };
  }, []);
  useLayoutEffect(() => {
    plot.current?.update(activity);
  }, [activity]);
  return (
    <section className="delivery-chart" aria-label="Hub 最近收发活动">
      <div className="delivery-chart-title">
        <strong>最近收发</strong>
        <span>{activity ? "最近 60 秒 · Records" : "活动状态暂不可用"}</span>
      </div>
      <div className="delivery-legend" aria-hidden="true">
        {deliveryStages.map(({ key, label }) => (
          <span key={key}>{label}</span>
        ))}
      </div>
      <div
        ref={host}
        className="delivery-plot"
        role="img"
        aria-label="最近收发数量，横轴时间，纵轴相邻更新之间的 Record 快照数量"
      />
      <output ref={summary} className="sr-only" aria-label="本次收发数量" />
    </section>
  );
}
