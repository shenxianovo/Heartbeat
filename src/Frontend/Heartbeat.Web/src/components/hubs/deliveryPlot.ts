import uPlot from "uplot";
import type { DeliveryActivity } from "@/api/hubs";
import { DeliverySamples, deliveryStages, displaySeconds } from "./deliverySamples";
import { createDeliveryTooltip } from "./deliveryTooltip";
import "uplot/dist/uPlot.min.css";

/** uPlot owns the canvas and animation clock; React only supplies the latest counters. */
export function mountDeliveryPlot(host: HTMLDivElement, summary: HTMLOutputElement) {
  const samples = new DeliverySamples();
  let observedAt = performance.now();
  let end = Date.now() / 1000;
  let frame = 0;
  let lastDraw = 0;
  const motion = matchMedia("(prefers-reduced-motion: reduce)");
  const tooltip = createDeliveryTooltip(host);
  const plot = new uPlot(
    {
      width: Math.max(1, host.clientWidth),
      height: 180,
      legend: { show: false },
      cursor: { y: false, points: { show: false }, drag: { setScale: false } },
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
        ...deliveryStages.map(({ color }, index) => ({
          stroke: color,
          width: 1.8,
          paths: uPlot.paths.spline!(),
          points: { show: false },
          spanGaps: false,
          dash: index === 1 ? [5, 3] : undefined,
        })),
      ],
      hooks: { setCursor: [(chart) => tooltip.update(chart, samples.points)] },
    },
    [[], [], []],
    host,
  );

  function draw() {
    const elapsed = (performance.now() - observedAt) / 1000;
    const right = end + elapsed;
    // Only move the scale between reports; reuse the current samples and Canvas.
    plot.setScale("x", { min: right - displaySeconds, max: right });
    host.dataset.range = `${right - displaySeconds}/${right}`;
    host.dataset.samples = String(samples.points.length);
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
      if (samples.observe(activity)) {
        end = activity!.capturedAt / 1000;
        observedAt = performance.now();
        const values = deliveryStages.map(({ key }) => samples.points.map((point) => point[key]));
        plot.setData([samples.points.map((point) => point.at), ...values], false);
        plot.setScale("y", {
          min: 0,
          max: Math.max(5, ...values.flat().map((value) => (value ?? 0) * 1.15)),
        });
        const current = samples.points.at(-1)!;
        summary.textContent =
          current.from == null
            ? "等待下一次更新"
            : deliveryStages.map(({ key, label }) => `${label} ${current[key]}`).join("，");
      }
      if (!activity) summary.textContent = "暂无数据";
      draw();
    },
    destroy() {
      cancelAnimationFrame(frame);
      resize.disconnect();
      plot.destroy();
      tooltip.destroy();
    },
  };
}
