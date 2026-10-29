import { describe, expect, it } from "vitest";
import type { Observation, Version } from "@/api/types";
import { groupThings, summarize, tickStep, ticks, withDrafts, type Experience } from "./day";

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
    observationId: id,
    observation: version<Observation>(id, {} as Observation),
    observerVersionId: "observer-v1",
    draftAt: null,
    writtenAt: end,
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

describe("groupThings", () => {
  it("两台设备上的同一款软件合成一行，时长相加，彼此之间不算切换", () => {
    const macA = version("qq-mac-a", {});
    const macB = version("qq-mac-b", {});
    const qq = version("qq", {});
    const grouped = groupThings(
      [
        experience("a", from, from + 10 * MIN, macA),
        experience("b", from + 10 * MIN, from + 15 * MIN, macB),
        experience("c", from + 15 * MIN, from + 20 * MIN, chrome),
      ],
      (thing) =>
        thing.version === macA || thing.version === macB ? { version: qq, title: "QQ", typeName: "应用" } : null,
    );
    const day = summarize(grouped, [], from, to);
    expect(day.things.map((row) => [row.key, row.total / MIN])).toEqual([
      ["qq", 15],
      ["chrome", 5],
    ]);
    expect(day.switches).toBe(1);
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

describe("withDrafts", () => {
  it("草稿取代同一份 Observation 的时段，保留已保存的版本；只有草稿的时段也列出", () => {
    const saved = experience("a", from, from + 10 * MIN, code);
    const draft = { ...experience("a", from, from + 30 * MIN, code), observation: null, draftAt: from + 30 * MIN };
    const fresh = { ...experience("b", from + 30 * MIN, from + 31 * MIN, chrome), observation: null, draftAt: from };
    const merged = withDrafts([saved, experience("c", from, from + MIN, null)], [draft, fresh]);
    expect(merged.map((e) => [e.observationId, e.end, e.observation?.entityId ?? null])).toEqual([
      ["c", from + MIN, "c"],
      ["a", from + 30 * MIN, "a"],
      ["b", from + 31 * MIN, null],
    ]);
    expect(summarize(merged, [], from, to).things.find((r) => r.key === "chrome")!.segments[0]!.ongoing).toBe(true);
  });
});
