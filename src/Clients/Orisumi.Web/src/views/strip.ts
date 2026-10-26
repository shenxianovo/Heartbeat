import { isActive } from "@/lib/activity";
import { ago, SECOND } from "@/lib/time";
import { WRITE_WINDOW } from "@/lib/writes";
import { color, hit, label, n, type Renderer } from "./common";

const NAME = 110;
const AGO = 70;
const ROW = 30;
const HOT = 1.5 * SECOND;

/** 行：每个活跃的 Observer 一行，最近 10 分钟的写入是竖线；安静的折叠成一行。 */
export const renderStrip: Renderer = ({ width, observers, now, selected, expanded }) => {
  const lane = width - NAME - AGO;
  const rows = [...observers].sort((a, b) => b.lastActivityAt - a.lastActivityAt);
  const active = rows.filter((o) => isActive(o, now));
  const quiet = rows.filter((o) => !isActive(o, now));
  const shown = expanded ? rows : active;

  let y = 26;
  let markup = label(NAME, 12, "10 分钟前", "muted") + label(NAME + lane, 12, "现在", "muted", { anchor: "end" });
  for (const observer of shown) {
    const c = color(observer);
    let inner = label(0, y + 14, observer.name, "secondary");
    inner += `<rect x="${NAME}" y="${y}" width="${lane}" height="20" rx="3" fill="var(--panel)"/>`;
    observer.writes.forEach((at, i) => {
      const age = Math.max(0, now - at);
      if (age > WRITE_WINDOW) return;
      const x = NAME + lane - (age / WRITE_WINDOW) * lane;
      const hot = now - observer.seen[i]! < HOT;
      const opacity = hot ? 1 : Math.max(0.25, 1 - age / WRITE_WINDOW);
      inner += `<rect x="${n(x - 1)}" y="${y + 4}" width="1.5" height="12" fill="${hot ? c : "var(--fg-3)"}" opacity="${n(opacity)}"/>`;
    });
    inner += label(width, y + 14, ago(now - observer.lastActivityAt), "muted", { anchor: "end" });
    markup += hit(observer, selected, inner);
    y += ROW;
  }

  if (quiet.length && !expanded) {
    const names = quiet
      .slice(0, 7)
      .map((o) => o.name)
      .join(" · ");
    markup +=
      `<g data-more="1"><rect x="0" y="${y - 4}" width="${width}" height="28" fill="transparent"/>` +
      label(0, y + 14, `另外 ${quiet.length} 个`, "secondary") +
      label(NAME, y + 14, quiet.length > 7 ? `${names} …` : names, "muted") +
      "</g>";
    y += ROW;
  } else if (expanded && quiet.length) {
    markup +=
      `<g data-more="1"><rect x="0" y="${y - 4}" width="${width}" height="28" fill="transparent"/>` +
      label(0, y + 14, "收起", "muted") +
      "</g>";
    y += ROW;
  }
  return { height: Math.max(140, y), markup };
};
