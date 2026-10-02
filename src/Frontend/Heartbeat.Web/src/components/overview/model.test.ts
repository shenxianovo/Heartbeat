import { expect, it } from "vitest";
import type { TimelineRecord } from "@/api/types";
import { overviewSources, summarizeSource, type OverviewSource } from "./model";

const source: OverviewSource = {
  id: "desktop",
  name: "Desktop",
  label: "Desktop",
  kind: "desktop",
};

function application(id: string, start: number, end: number): TimelineRecord {
  return {
    id,
    startedAt: new Date(start).toISOString(),
    endedAt: new Date(end).toISOString(),
    observedAt: null,
    receivedAt: new Date(end).toISOString(),
    value: {},
    objects: [{ id, role: "application", namespace: "app.macos.bundle_id", key: id, name: null }],
  };
}

it("counts only applications with observations inside the half-open window", () => {
  const records = [
    application("before", 0, 10),
    application("inside", 12, 18),
    application("after", 20, 30),
  ];
  expect(summarizeSource(source, records, 10, 20)).toMatchObject({
    milliseconds: 6,
    subjects: 1,
    invalid: 0,
  });
});

it("counts overlapping observations once across producers while preserving gaps", () => {
  const records = [
    application("app", 8, 14),
    application("app", 12, 16),
    application("another", 18, 25),
  ];
  const original = structuredClone(records);
  expect(summarizeSource(source, records, 10, 20)).toMatchObject({
    milliseconds: 8,
    subjects: 2,
    invalid: 0,
  });
  expect(records).toEqual(original);
});

it("splits observations at local midnight, unions overlap and clips partial days", () => {
  const at = (day: number, hour: number) => new Date(2026, 8, day, hour).getTime();
  const result = summarizeSource(
    source,
    [
      application("overnight", at(12, 22), at(13, 2)),
      application("overlap", at(13, 1), at(13, 4)),
      application("last", at(15, 0), at(15, 5)),
    ],
    at(12, 23),
    at(15, 2),
  );
  expect(result.days).toEqual([
    { from: at(12, 23), to: at(13, 0), milliseconds: 3_600_000 },
    { from: at(13, 0), to: at(14, 0), milliseconds: 4 * 3_600_000 },
    { from: at(14, 0), to: at(15, 0), milliseconds: 0 },
    { from: at(15, 0), to: at(15, 2), milliseconds: 2 * 3_600_000 },
  ]);
  expect(result.days.reduce((sum, day) => sum + day.milliseconds, 0)).toBe(result.milliseconds);
});

it("selects overview entries from observed roles and retains unfamiliar object types", () => {
  const objects = [
    { id: "mac", namespace: "device", roles: ["device"] },
    { id: "account", namespace: "vrchat.account", roles: ["account", "friend"] },
    { id: "friend", namespace: "vrchat.account", roles: ["friend"] },
    { id: "world", namespace: "vrchat.world", roles: ["world"] },
    { id: "app", namespace: "app.macos.bundle_id", roles: ["application"] },
    { id: "steps", namespace: "wechat.account", roles: ["account"] },
    { id: "project", namespace: "example.project", roles: ["project"] },
  ].map((item) => ({ ...item, key: item.id, name: null }));
  expect(overviewSources(objects).map((item) => item.id)).toEqual([
    "mac",
    "account",
    "steps",
    "project",
  ]);
});
