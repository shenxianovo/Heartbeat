import type { ObserverActivity } from "@/lib/activity";
import { categoryInfo } from "@/lib/categories";

/** 容器宽度未知时使用的宽度。视图按容器的实际像素宽度绘制，文字保持原始字号。 */
export const WIDTH = 900;

export interface ViewInput {
  width: number;
  observers: ObserverActivity[];
  now: number;
  selected: string | null;
  reduced: boolean;
  dayStart: number;
  observationCount: number;
  expanded: boolean;
}

export interface ViewOutput {
  height: number;
  markup: string;
}

export type Renderer = (input: ViewInput) => ViewOutput;

export function esc(text: string): string {
  return text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
}

export function n(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

export function color(observer: ObserverActivity): string {
  return categoryInfo(observer.category).color;
}

/** 选中其他 Observer 时降到 20% 不透明度。 */
export function dim(observer: ObserverActivity, selected: string | null): string {
  return selected !== null && selected !== observer.key ? ' opacity="0.2"' : "";
}

export function label(
  x: number | string,
  y: number | string,
  text: string,
  style: "primary" | "secondary" | "muted",
  options: { anchor?: "start" | "middle" | "end"; size?: number; weight?: number } = {},
): string {
  const fill = style === "primary" ? "var(--fg)" : style === "secondary" ? "var(--fg-2)" : "var(--fg-3)";
  const size = options.size ?? (style === "muted" ? 11 : 12);
  const anchor = options.anchor && options.anchor !== "start" ? ` text-anchor="${options.anchor}"` : "";
  const weight = options.weight ? `;font-weight:${options.weight}` : "";
  return `<text x="${x}" y="${y}"${anchor} style="font-size:${size}px;fill:${fill}${weight}">${esc(text)}</text>`;
}

export function hit(observer: ObserverActivity, selected: string | null, inner: string, extra = ""): string {
  return `<g data-key="${esc(observer.key)}"${dim(observer, selected)}${extra}><title>${esc(observer.name)}</title>${inner}</g>`;
}
