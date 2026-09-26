import type uPlot from "uplot";
import { densityHeight, type DensitySeries } from "./densitySeries";

/** Keep observed zeros and unknown gaps distinct; anchor each observed run to its true edges. */
export function densityPlotData(series: DensitySeries | null, peak: number): uPlot.AlignedData {
  const x: number[] = [];
  const y: (number | null)[] = [];
  const samples = series?.samples ?? [];
  samples.forEach((sample, index) => {
    const value = sample.covered ? densityHeight(sample.count, peak) : null;
    if (sample.covered && !samples[index - 1]?.covered) {
      x.push(sample.start);
      y.push(value);
    }
    x.push((sample.start + sample.end) / 2);
    y.push(value);
    if (sample.covered && !samples[index + 1]?.covered) {
      x.push(sample.end);
      y.push(value);
    }
  });
  return [x, y];
}
