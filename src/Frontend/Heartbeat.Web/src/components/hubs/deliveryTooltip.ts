import type uPlot from "uplot";
import { deliveryStages, type DeliveryPoint } from "./deliverySamples";

export function createDeliveryTooltip(host: HTMLElement) {
  const element = document.createElement("div");
  element.className = "delivery-tooltip";
  element.setAttribute("role", "tooltip");
  element.hidden = true;
  element.innerHTML = `<time></time><table><tbody>${deliveryStages
    .map(({ label }) => `<tr><th scope="row">${label}</th><td></td></tr>`)
    .join("")}</tbody></table>`;
  host.parentElement!.append(element);
  const time = element.querySelector("time")!;
  const cells = element.querySelectorAll("td");
  const formatTime = new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format;
  return {
    update(plot: uPlot, points: DeliveryPoint[]) {
      const { left = -1, top = -1 } = plot.cursor;
      const point = points[plot.posToIdx(left)];
      element.hidden = true;
      if (!point || left < 0 || top < 0 || Math.abs(plot.posToVal(left, "x") - point.at) > 0.5)
        return;
      element.hidden = false;
      time.textContent =
        point.from == null
          ? "等待下一次更新"
          : `${formatTime(point.from * 1000)} – ${formatTime(point.at * 1000)}`;
      deliveryStages.forEach(({ key }, series) => {
        cells[series]!.textContent = point[key]?.toLocaleString("zh-CN") ?? "—";
      });
      element.style.top = `${host.offsetTop + 8}px`;
      element.style.left = `${host.offsetLeft + (left < plot.over.clientWidth / 2 ? host.clientWidth - element.offsetWidth - 8 : 8)}px`;
    },
    destroy: () => element.remove(),
  };
}
