import type { Observation, Version } from "@/api/types";
import { thingColor } from "./roles";

// 一天的经历：有时段的 Observation 按它的内容引用的事物分行；不引用事物的按内容类型分行；
// 只有时间点的 Observation 按 Observer 汇成一行刻度。

/** 解析过的一份经历。things 是内容引用的事物（例如本机应用）。 */
export interface Experience {
  observation: Version<Observation>;
  content: Version | null;
  contentType: string;
  contentTypeName: string;
  contentTitle: string | null;
  things: { version: Version; title: string; typeName: string }[];
  observerEntityId: string | null;
  start: number;
  end: number;
}

export interface PointGroup {
  key: string;
  title: string;
  times: number[];
}

export interface Segment {
  start: number;
  end: number;
  observationId: string;
  contentEntityId: string | null;
  label: string;
}

export interface Row {
  key: string;
  /** 事物的行有 thing；内容类型的行没有。 */
  thing: Version | null;
  title: string;
  typeName: string;
  color: string | null;
  total: number;
  segments: Segment[];
}

export interface DaySummary {
  things: Row[];
  lanes: Row[];
  points: PointGroup[];
  /** 事物之间切换的次数：同一个 Observer 的相邻两段指向不同的事物。 */
  switches: number;
  /** 事物的行所属的类型名，例如“本机应用”。 */
  thingTypes: string[];
}

export function clip(start: number, end: number, from: number, to: number): [number, number] | null {
  const s = Math.max(start, from);
  const e = Math.min(end, to);
  return e > s ? [s, e] : null;
}

export function summarize(
  experiences: readonly Experience[],
  points: readonly PointGroup[],
  from: number,
  to: number,
): DaySummary {
  const things = new Map<string, Row>();
  const lanes = new Map<string, Row>();
  const sorted = [...experiences].sort((a, b) => a.start - b.start);
  let switches = 0;
  const last = new Map<string, string>();

  for (const experience of sorted) {
    const range = clip(experience.start, experience.end, from, to);
    if (!range) continue;
    const segment = (label: string): Segment => ({
      start: range[0],
      end: range[1],
      observationId: experience.observation.entityId,
      contentEntityId: experience.content?.entityId ?? null,
      label,
    });
    if (experience.things.length) {
      for (const { version, title, typeName } of experience.things) {
        const row = things.get(version.entityId) ?? {
          key: version.entityId,
          thing: version,
          title,
          typeName,
          color: thingColor(title),
          total: 0,
          segments: [],
        };
        row.segments.push(segment(experience.contentTitle ?? title));
        row.total += range[1] - range[0];
        things.set(version.entityId, row);
      }
      const observer = experience.observerEntityId ?? "";
      const target = experience.things[0]!.version.entityId;
      if (last.has(observer) && last.get(observer) !== target) switches++;
      last.set(observer, target);
    } else {
      const row = lanes.get(experience.contentType) ?? {
        key: experience.contentType,
        thing: null,
        title: experience.contentTypeName,
        typeName: experience.contentTypeName,
        color: null,
        total: 0,
        segments: [],
      };
      row.segments.push(segment(experience.contentTitle ?? experience.contentTypeName));
      row.total += range[1] - range[0];
      lanes.set(experience.contentType, row);
    }
  }

  const thingRows = [...things.values()].sort((a, b) => b.total - a.total);
  return {
    things: thingRows,
    lanes: [...lanes.values()].sort((a, b) => b.total - a.total),
    points: points
      .map((group) => ({ ...group, times: group.times.filter((t) => t >= from && t < to) }))
      .filter((group) => group.times.length),
    switches,
    thingTypes: [...new Set(thingRows.map((row) => row.typeName).filter(Boolean))],
  };
}

type Thing = Experience["things"][number];

/** 事物换成什么，例如本机应用换成它所属的应用产品；返回 null 时保持原样。 */
export type ThingGroup = (thing: Thing) => Thing | null;

/** 把经历引用的事物换成它们的合并对象，例如两台 Mac 上的 QQ 都换成 QQ 这个产品。同一份经历里重复的只留一个。 */
export function groupThings(experiences: readonly Experience[], group: ThingGroup): Experience[] {
  return experiences.map((experience) => {
    if (!experience.things.length) return experience;
    const things = new Map<string, Thing>();
    for (const thing of experience.things) {
      const target = group(thing) ?? thing;
      if (!things.has(target.version.entityId)) things.set(target.version.entityId, target);
    }
    return { ...experience, things: [...things.values()] };
  });
}

const TICK_STEPS = [1, 5, 10, 15, 30, 60, 120, 180, 360].map((minutes) => minutes * 60_000);

/** 刻度的间隔：让 [from, to) 里大约有 target 个刻度。 */
export function tickStep(span: number, target = 8): number {
  return TICK_STEPS.find((step) => span / step <= target) ?? TICK_STEPS.at(-1)!;
}

/** 在 [from, to) 里按本地时间对齐的刻度。 */
export function ticks(from: number, to: number, step: number): number[] {
  const out: number[] = [];
  const day = new Date(from);
  const midnight = new Date(day.getFullYear(), day.getMonth(), day.getDate()).getTime();
  for (let t = midnight + Math.ceil((from - midnight) / step) * step; t < to; t += step) out.push(t);
  return out;
}
