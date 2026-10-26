import { categoryInfo, type CategoryKey } from "@/lib/categories";
import { SECOND } from "@/lib/time";
import { label, n, type Renderer } from "./common";

const HEIGHT = 320;
const BASELINE = 245;
const PERIOD = 20 * SECOND;
const BINS = 100;
const ERASE = 24;

/** 心电：最近 20 秒的写入，指针从左向右扫。 */
export const renderEcg: Renderer = ({ width, observers, now, selected, reduced }) => {
  const bins: CategoryKey[][] = Array.from({ length: BINS }, () => []);
  let perMinute = 0;
  for (const observer of observers) {
    if (selected !== null && selected !== observer.key) continue;
    for (const at of observer.writes) {
      const age = now - at;
      if (age < 60 * SECOND) perMinute += 1;
      if (age >= PERIOD - 0.7 * SECOND || age < 0) continue;
      const position = reduced ? (PERIOD - age) / PERIOD : (((at % PERIOD) + PERIOD) % PERIOD) / PERIOD;
      bins[Math.min(BINS - 1, Math.floor(position * BINS))]!.push(observer.category);
    }
  }

  let markup = "";
  for (let x = 0; x <= width; x += width / 20) {
    markup += `<line x1="${n(x)}" y1="80" x2="${n(x)}" y2="310" stroke="var(--line)"/>`;
  }
  for (let y = 90; y <= 310; y += 44) markup += `<line x1="0" y1="${y}" x2="${width}" y2="${y}" stroke="var(--line)"/>`;

  const binWidth = width / BINS;
  const cursor = reduced ? width : ((now % PERIOD) / PERIOD) * width;
  const segments: string[] = [];
  let path = "";
  let dots = "";
  for (let i = 0; i < BINS; i++) {
    const x0 = i * binWidth;
    const x1 = x0 + binWidth;
    const erased =
      !reduced &&
      ((x0 >= cursor - 2 && x0 < cursor + ERASE) || (cursor + ERASE > width && x0 < cursor + ERASE - width));
    if (erased) {
      if (path) segments.push(path);
      path = "";
      continue;
    }
    if (!path) path = `M${n(x0)} ${BASELINE}`;
    const bin = bins[i]!;
    if (!bin.length) {
      path += ` L${n(x1)} ${BASELINE}`;
      continue;
    }
    const h = Math.min(150, 26 + 18 * bin.length);
    const xm = x0 + binWidth / 2;
    path +=
      ` L${n(xm - 3)} ${BASELINE} L${n(xm - 1.5)} ${BASELINE + 8} L${n(xm)} ${n(BASELINE - h)}` +
      ` L${n(xm + 2)} ${n(BASELINE + h * 0.28)} L${n(xm + 4)} ${BASELINE} L${n(x1)} ${BASELINE}`;
    dots += `<circle cx="${n(xm)}" cy="${n(BASELINE - h - 7)}" r="2.6" fill="${categoryInfo(dominant(bin)).color}"/>`;
  }
  if (path) segments.push(path);

  markup += `<path d="${segments.join(" ")}" fill="none" stroke="var(--fg)" stroke-width="1.4" stroke-linejoin="round"/>`;
  markup += dots;
  if (!reduced) markup += `<circle cx="${n(cursor)}" cy="${BASELINE}" r="3.5" fill="var(--fg)"/>`;
  const selectedName = selected !== null ? observers.find((o) => o.key === selected)?.name : undefined;
  markup += label(0, 36, String(perMinute), "primary", { size: 32, weight: 500 });
  markup += label(0, 58, selectedName ? `每分钟写入 · ${selectedName}` : "每分钟写入", "muted");
  markup += label(width, 58, "最近 20 秒", "muted", { anchor: "end" });
  return { height: HEIGHT, markup };
};

function dominant(bin: CategoryKey[]): CategoryKey {
  const counts = new Map<CategoryKey, number>();
  for (const key of bin) counts.set(key, (counts.get(key) ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]![0];
}
