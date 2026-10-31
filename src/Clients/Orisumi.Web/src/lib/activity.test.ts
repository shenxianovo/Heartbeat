import { describe, expect, it } from "vitest";
import { observation } from "@/test/fixtures";
import { isActive, observerRows, union, type ActivityInput } from "./activity";
import { observerIdentity, UNREADABLE_IDENTITY, type ObserverIdentity } from "./observers";
import { MINUTE, SECOND } from "./time";

const now = Date.parse("2026-10-25T06:00:00.000Z");
const at = (offset: number) => new Date(now + offset).toISOString();

const foreground = observerIdentity("fg", "前台状态 Observer", "foreground-state-observers");
const identity = observerIdentity("id", "应用识别 Observer", "application-identity-observers");
const versions: Record<string, ObserverIdentity> = {
  "fg-v1": foreground,
  "fg-v2": foreground,
  "id-v1": identity,
  gone: UNREADABLE_IDENTITY,
};

function input(overrides: Partial<ActivityInput> = {}): ActivityInput {
  return {
    observers: [foreground, identity],
    timedTypes: new Set(["foreground-state-observers"]),
    identityOf: (versionId) => versions[versionId],
    timed: [],
    recorded: [],
    now,
    ...overrides,
  };
}

describe("observerRows", () => {
  it("列出的 Observer 今天没有写入也有一行", () => {
    const rows = observerRows(input());
    expect(rows.map((r) => [r.name, r.count, r.lastAt])).toEqual([
      ["前台状态 Observer", 0, 0],
      ["应用识别 Observer", 0, 0],
    ]);
  });

  it("写有事实时间的 Observer 按事实时间画，结束未知的时段画到现在；同一实体的不同版本算一个 Observer", () => {
    const rows = observerRows(
      input({
        timed: [
          observation({
            recordedAt: at(-30 * MINUTE),
            content: { observerVersionId: "fg-v1", startAt: at(-60 * MINUTE), endAt: at(-30 * MINUTE) },
          }),
          observation({
            recordedAt: at(-5 * MINUTE),
            content: { observerVersionId: "fg-v2", startAt: at(-5 * MINUTE), endAt: null },
          }),
        ],
      }),
    );
    const row = rows.find((r) => r.key === "fg")!;
    expect(row.basis).toBe("fact");
    expect(row.count).toBe(2);
    expect(row.spans).toEqual([
      { start: now - 60 * MINUTE, end: now - 30 * MINUTE },
      { start: now - 5 * MINUTE, end: now },
    ]);
    expect(row.ticks).toEqual([]);
    expect(row.lastAt).toBe(now - 5 * MINUTE);
  });

  it("只知道一个边界的 Observation 是一道刻度", () => {
    const rows = observerRows(
      input({
        timed: [observation({ content: { observerVersionId: "fg-v1", startAt: null, endAt: at(-MINUTE) } })],
      }),
    );
    expect(rows.find((r) => r.key === "fg")!.ticks).toEqual([now - MINUTE]);
  });

  it("只写事实时间未知的陈述的 Observer 按保存时间画，Observation 来自引用方", () => {
    const rows = observerRows(
      input({
        recorded: [
          observation({
            recordedAt: at(-18 * MINUTE),
            content: { observerVersionId: "id-v1", startAt: null, endAt: null },
          }),
        ],
      }),
    );
    const row = rows.find((r) => r.key === "id")!;
    expect(row.basis).toBe("recorded");
    expect(row.ticks).toEqual([now - 18 * MINUTE]);
    expect(row.count).toBe(1);
  });

  it("最近 60 秒内保存过 Observation 的 Observer 算正在写入，排在前面", () => {
    const rows = observerRows(
      input({
        timed: [
          observation({
            recordedAt: at(-SECOND),
            content: { observerVersionId: "fg-v1", startAt: at(-9 * MINUTE), endAt: at(-SECOND) },
          }),
        ],
        recorded: [observation({ recordedAt: at(-MINUTE * 2), content: { observerVersionId: "id-v1" } })],
      }),
    );
    expect(rows[0]!.key).toBe("fg");
    expect(rows[0]!.lastAt).toBe(now - SECOND);
    expect(isActive(rows[0]!, now)).toBe(true);
    expect(isActive(rows[0]!, now + 61 * SECOND)).toBe(false);
    expect(isActive(rows[1]!, now)).toBe(false);
  });

  it("读不到 Observer 版本的 Observation 合成一行不可读取；还没解析的暂不计入", () => {
    const rows = observerRows(
      input({
        timed: [
          observation({ content: { observerVersionId: "gone" } }),
          observation({ content: { observerVersionId: "gone" } }),
          observation({ content: { observerVersionId: "pending" } }),
        ],
      }),
    );
    expect(rows.map((r) => [r.name, r.count])).toContainEqual(["不可读取", 2]);
    expect(rows).toHaveLength(3);
  });
});

describe("union", () => {
  it("合并重叠和间隔不超过 gap 的时段", () => {
    expect(
      union(
        [
          { start: 10, end: 20 },
          { start: 0, end: 12 },
          { start: 21, end: 30 },
          { start: 40, end: 50 },
        ],
        1,
      ),
    ).toEqual([
      { start: 0, end: 30 },
      { start: 40, end: 50 },
    ]);
  });
});
