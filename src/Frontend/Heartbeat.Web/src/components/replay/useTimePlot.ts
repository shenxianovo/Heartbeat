import { useLayoutEffect, useRef } from "react";
import uPlot from "uplot";
import type { TimeRange } from "./timeRange";
import "uplot/dist/uPlot.min.css";

export interface TimePlotModel {
  data: uPlot.AlignedData;
  range: TimeRange;
  paths: uPlot.Series.PathBuilder;
  stroke?: (plot: uPlot) => string;
  fill?: (plot: uPlot) => CanvasGradient | string;
  draw?: (plot: uPlot) => void;
}

/** A controlled time plot: React owns the viewport; uPlot owns its drawing surface. */
export function useTimePlot(model: TimePlotModel) {
  const host = useRef<HTMLDivElement>(null);
  const chart = useRef<uPlot | null>(null);
  const latest = useRef(model);
  useLayoutEffect(() => {
    latest.current = model;
    const plot = chart.current;
    if (plot) synchronize(plot, model);
  }, [model]);
  useLayoutEffect(() => {
    const element = host.current!;
    function resize() {
      const width = element.clientWidth;
      const height = element.clientHeight;
      if (!width || !height) return;
      if (chart.current) {
        chart.current.setSize({ width, height });
        return;
      }
      chart.current = new uPlot(
        {
          width,
          height,
          padding: [0, 0, 0, 0],
          cursor: { show: false },
          select: { show: false, left: 0, top: 0, width: 0, height: 0 },
          legend: { show: false },
          axes: [{ show: false }, { show: false }],
          scales: { x: { time: false, auto: false }, y: { auto: false, range: [-0.09, 1.12] } },
          series: [
            {},
            {
              paths: (plot, index, from, to) => latest.current.paths(plot, index, from, to),
              stroke: (plot) => latest.current.stroke?.(plot) ?? "transparent",
              fill: (plot) => latest.current.fill?.(plot) ?? "transparent",
              width: 1.5,
              points: { show: false },
              spanGaps: false,
            },
          ],
          hooks: {
            draw: [
              (plot) => {
                latest.current.draw?.(plot);
                element.dataset.range = `${plot.scales.x!.min}/${plot.scales.x!.max}`;
              },
            ],
          },
        },
        [[], []],
        element,
      );
      synchronize(chart.current, latest.current);
    }
    resize();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(resize);
    observer?.observe(element);
    // Repaint with the current CSS palette without replacing the chart instance.
    const theme = new MutationObserver(() => chart.current?.redraw());
    theme.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class", "style"],
    });
    return () => {
      observer?.disconnect();
      theme.disconnect();
      chart.current?.destroy();
      chart.current = null;
    };
  }, []);
  return { host, chart };
}

function synchronize(plot: uPlot, model: TimePlotModel) {
  plot.batch(() => {
    plot.setData(model.data, false);
    plot.setScale("x", { min: model.range.start, max: model.range.end });
    plot.setScale("y", { min: -0.09, max: 1.12 });
  });
}

export function plotColor(plot: uPlot, token: string) {
  return getComputedStyle(plot.root.isConnected ? plot.root : document.documentElement)
    .getPropertyValue(token)
    .trim();
}
