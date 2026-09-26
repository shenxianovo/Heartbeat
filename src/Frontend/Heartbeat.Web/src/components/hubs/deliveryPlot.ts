import uPlot from "uplot";
import type { DeliveryActivity } from "@/api/hubs";
import "uplot/dist/uPlot.min.css";

const windowSeconds = 60;
const stages = ["received", "sent", "confirmed"] as const;
const colors = ["#38a9e8", "#ed9a38", "#2fbc95"];

/** uPlot owns the canvas and animation clock; React only supplies source-time buckets. */
export function mountDeliveryPlot(host: HTMLDivElement) {
  let latest: DeliveryActivity | undefined;
  let observedAt = performance.now();
  let end = Date.now() / 1000;
  let frame = 0;
  let lastDraw = 0;
  let online = false;
  const motion = matchMedia("(prefers-reduced-motion: reduce)");
  const plot = new uPlot(
    {
      width: Math.max(1, host.clientWidth),
      height: 180,
      legend: { show: false },
      cursor: { show: false },
      select: { show: false, left: 0, top: 0, width: 0, height: 0 },
      scales: {
        x: { time: false, auto: false },
        y: { range: (_plot, _min, max) => [0, Math.max(5, Math.ceil(max * 1.15))] },
      },
      axes: [
        {
          stroke: "#89949f",
          grid: { show: false },
          size: 28,
          splits: (chart) => [
            chart.scales.x!.max! - 60,
            chart.scales.x!.max! - 30,
            chart.scales.x!.max!,
          ],
          values: () => ["−60 秒", "−30 秒", "现在"],
        },
        {
          stroke: "#89949f",
          size: 38,
          grid: { stroke: "#89949f20", width: 1 },
          values: (_plot, ticks) =>
            ticks.map((value) => (Number.isInteger(value) ? String(value) : "")),
        },
      ],
      series: [
        {},
        ...stages.map((_stage, index) => ({
          stroke: colors[index],
          width: 1.8,
          points: { show: true, size: 3 },
          spanGaps: false,
          dash: index === 1 ? [5, 3] : undefined,
        })),
      ],
    },
    [[], [], [], []],
    host,
  );

  function draw() {
    const elapsed = (performance.now() - observedAt) / 1000;
    const right = end + elapsed;
    // Only move the scale between reports; reuse the same 60 points and Canvas.
    plot.setScale("x", { min: right - windowSeconds, max: right });
    host.dataset.range = `${right - windowSeconds}/${right}`;
    host.dataset.samples = String(latest?.buckets.length ?? 0);
  }
  function animate(now: number) {
    if (!document.hidden && now - lastDraw >= (motion.matches ? 1000 : 50)) {
      draw();
      lastDraw = now;
    }
    frame = requestAnimationFrame(animate);
  }
  const resize = new ResizeObserver(() =>
    plot.setSize({ width: Math.max(1, host.clientWidth), height: 180 }),
  );
  resize.observe(host);
  frame = requestAnimationFrame(animate);
  return {
    update(activity?: DeliveryActivity) {
      if (activity) {
        const current = end + (performance.now() - observedAt) / 1000;
        end =
          activity.epoch === latest?.epoch && online
            ? Math.max(current, activity.capturedAt / 1000)
            : activity.capturedAt / 1000;
        latest = activity;
        observedAt = performance.now();
        const values = stages.map((stage) => activity.buckets.map((bucket) => bucket[stage]));
        plot.setData([activity.buckets.map((bucket) => bucket.second), ...values], false);
        plot.setScale("y", {
          min: 0,
          max: Math.max(5, ...values.flat().map((value) => value * 1.15)),
        });
      }
      online = !!activity;
      draw();
    },
    destroy() {
      cancelAnimationFrame(frame);
      resize.disconnect();
      plot.destroy();
    },
  };
}
