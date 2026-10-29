import { describe, expect, it, vi } from "vitest";
import { observation } from "@/test/fixtures";
import { ActivityStore, hourly, isActive, type ObserverActivity, type Segment } from "./activity";
import type { ObserverIdentity } from "./observers";
import { HOUR, SECOND } from "./time";

const now = Date.parse("2026-10-25T06:00:00.000Z");
const at = (offset: number) => new Date(now + offset).toISOString();

function store(): ActivityStore {
  return new ActivityStore(async (versionId): Promise<ObserverIdentity> => ({
    key: versionId === "b-v1" ? "b" : "a",
    entityId: versionId === "b-v1" ? "b" : "a",
    typeName: versionId === "b-v1" ? "设备状态" : "前台状态",
    resourceName: "foreground-state-observers",
    category: "desktop",
  }));
}

describe("ActivityStore", () => {
  it("按 Observer 实体归并 Observation 和写入，同一实体的不同版本算一个 Observer", async () => {
    const activity = store();
    activity.ingestToday(
      [
        observation({ recordedAt: at(-5 * SECOND), content: { observerVersionId: "a-v1" } }),
        observation({ recordedAt: at(-2 * 3600 * SECOND), content: { observerVersionId: "a-v2" } }),
        observation({ recordedAt: at(-30 * SECOND), content: { observerVersionId: "b-v1" } }),
      ],
      now,
    );
    await vi.waitFor(() => expect(activity.snapshot(now).observers).toHaveLength(2));

    const snapshot = activity.snapshot(now);
    const a = snapshot.observers.find((o) => o.key === "a")!;
    expect(a.name).toBe("前台状态");
    expect(a.observationCount).toBe(2);
    expect(a.writes).toEqual([now - 5 * SECOND]);
    expect(a.lastActivityAt).toBe(now - 5 * SECOND);
    expect(snapshot.observationCount).toBe(3);
  });

  it("“现在”的结果合并进今天的数据，并记下新的写入", async () => {
    const activity = store();
    const first = observation({ recordedAt: at(-90 * SECOND), content: { observerVersionId: "a-v1" } });
    activity.ingestToday([first], now);
    activity.ingestLive([{ ...first, versionId: "next", recordedAt: at(-1 * SECOND) }], now);
    await vi.waitFor(() => expect(activity.snapshot(now + 1).observers).toHaveLength(1));

    const a = activity.snapshot(now + 2 * SECOND).observers[0]!;
    expect(a.observationCount).toBe(1);
    expect(a.writes).toEqual([now - 90 * SECOND, now - 1 * SECOND]);
    expect(a.seen.at(-1)).toBe(now);
    expect(isActive(a, now)).toBe(true);
    expect(isActive(a, now + 61 * SECOND)).toBe(false);
  });
});

describe("hourly", () => {
  const dayStart = Date.parse("2026-10-24T16:00:00.000Z");
  const segment = (start: number | null, end: number | null): Segment => ({
    entityId: "o",
    start: start === null ? null : dayStart + start,
    end: end === null ? null : dayStart + end,
    recordedAt: dayStart,
  });
  const withSegments = (segments: Segment[]) => [{ segments } as ObserverActivity];

  it("跨小时的 Observation 在经过的每个小时各算一次，正好结束在整点的不算下一小时", () => {
    const hours = hourly(withSegments([segment(1.5 * HOUR, 3 * HOUR)]), dayStart);
    expect(hours.slice(0, 4)).toEqual([0, 1, 1, 0]);
  });

  it("只知道一个边界的算在那个小时，超出今天的截到首尾", () => {
    const hours = hourly(
      withSegments([segment(null, 5.2 * HOUR), segment(-2 * HOUR, 0.5 * HOUR), segment(23.5 * HOUR, 26 * HOUR)]),
      dayStart,
    );
    expect(hours[5]).toBe(1);
    expect(hours[0]).toBe(1);
    expect(hours[23]).toBe(1);
    expect(hours.reduce((a, b) => a + b, 0)).toBe(3);
  });
});
