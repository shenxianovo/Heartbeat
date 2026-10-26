import { describe, expect, it } from "vitest";
import { observation } from "@/test/fixtures";
import { MINUTE, SECOND } from "./time";
import { detectWrites, pruneWrites } from "./writes";

const now = Date.parse("2026-10-25T06:00:00.000Z");
const at = (offset: number) => new Date(now + offset).toISOString();

describe("detectWrites", () => {
  it("新出现的 Observation 记为一次写入，时刻取 recordedAt", () => {
    const known = new Map<string, string>();
    const v = observation({ recordedAt: at(-5 * SECOND) });
    const writes = detectWrites(known, [v], now);
    expect(writes).toEqual([
      {
        at: now - 5 * SECOND,
        seenAt: now,
        entityId: v.entityId,
        versionId: v.versionId,
        observerVersionId: "observer-v1",
      },
    ]);
    expect(known.get(v.entityId)).toBe(v.versionId);
  });

  it("版本没有变化时不记写入，版本变化时记一次", () => {
    const known = new Map<string, string>();
    const first = observation({ recordedAt: at(-10 * SECOND) });
    detectWrites(known, [first], now);
    expect(detectWrites(known, [first], now)).toEqual([]);

    const second = { ...first, versionId: "next-version", recordedAt: at(-1 * SECOND) };
    expect(detectWrites(known, [second], now).map((w) => w.versionId)).toEqual(["next-version"]);
  });

  it("超过 10 分钟的写入只更新已知版本", () => {
    const known = new Map<string, string>();
    const old = observation({ recordedAt: at(-11 * MINUTE) });
    expect(detectWrites(known, [old], now)).toEqual([]);
    expect(known.has(old.entityId)).toBe(true);
  });

  it("晚于当前时刻的 recordedAt 按当前时刻放置", () => {
    const writes = detectWrites(new Map(), [observation({ recordedAt: at(3 * SECOND) })], now);
    expect(writes[0]!.at).toBe(now);
  });

  it("第一次读取时发现的写入不算新发现", () => {
    const writes = detectWrites(new Map(), [observation({ recordedAt: at(-30 * SECOND) })], now, true);
    expect(writes[0]!.seenAt).toBe(now - 30 * SECOND);
  });
});

describe("pruneWrites", () => {
  it("只保留最近 10 分钟", () => {
    const base = { entityId: "e", versionId: "v", observerVersionId: "o" };
    const writes = [
      { ...base, at: now - 11 * MINUTE, seenAt: now },
      { ...base, at: now - 9 * MINUTE, seenAt: now },
    ];
    expect(pruneWrites(writes, now).map((w) => w.at)).toEqual([now - 9 * MINUTE]);
  });
});
