import { describe, expect, it } from "vitest";
import type { Observation, Version } from "@/api/types";
import { summarize, tickStep, ticks, type Experience } from "./day";

const MIN = 60_000;
const from = Date.parse("2026-10-29T00:00:00Z");
const to = from + 24 * 60 * MIN;

function version<C>(entityId: string, content: C): Version<C> {
  return {
    entityId,
    versionId: `${entityId}-v1`,
    previousVersionId: null,
    category: "business",
    schemaVersionId: null,
    recordedAt: "2026-10-29T00:00:00Z",
    content,
  };
}

const chrome = version("chrome", {});
const code = version("code", {});

function experience(id: string, start: number, end: number, thing: Version | null): Experience {
  return {
    observation: version<Observation>(id, {} as Observation),
    content: null,
    contentType: "periods",
    contentTypeName: "前台应用时段",
    contentTitle: null,
    things: thing ? [{ version: thing, title: thing.entityId, typeName: "本机应用" }] : [],
    observerEntityId: "observer",
    start,
    end,
  };
}

describe("summarize", () => {
  it("按事物分行、按时长排序，数出相邻两段之间的切换", () => {
    const day = summarize(
      [
        experience("a", from + 60 * MIN, from + 70 * MIN, chrome),
        experience("b", from + 70 * MIN, from + 100 * MIN, code),
        experience("c", from + 100 * MIN, from + 105 * MIN, chrome),
        experience("d", from + 105 * MIN, from + 106 * MIN, null),
      ],
      [],
      from,
      to,
    );
    expect(day.things.map((row) => [row.key, row.total / MIN])).toEqual([
      ["code", 30],
      ["chrome", 15],
    ]);
    expect(day.switches).toBe(2);
    expect(day.lanes).toHaveLength(1);
  });

  it("跨过这一天边界的时段只算这一天里的部分", () => {
    const day = summarize([experience("a", from - 30 * MIN, from + 30 * MIN, chrome)], [], from, to);
    expect(day.things[0]!.total).toBe(30 * MIN);
    expect(day.things[0]!.segments[0]!.start).toBe(from);
  });
});

describe("刻度", () => {
  it("间隔随范围变化，刻度对齐到整点", () => {
    expect(tickStep(24 * 60 * MIN)).toBe(180 * MIN);
    expect(tickStep(60 * MIN)).toBe(10 * MIN);
    const local = new Date(2026, 9, 29, 9, 7).getTime();
    const marks = ticks(local, local + 60 * MIN, 15 * MIN);
    expect(new Date(marks[0]!).getMinutes()).toBe(15);
  });
});
