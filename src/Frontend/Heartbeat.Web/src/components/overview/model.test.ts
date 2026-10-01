import { expect, it } from "vitest";
import type { TimelineRecord } from "@/api/types";
import { summarizeSource, type OverviewSource } from "./model";

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
    value: {
      device_id: "device",
      application: { platform: "macos", id_kind: "bundle_id", id },
    },
  };
}

it("counts only applications with observations inside the half-open window", () => {
  const records = [
    application("before", 0, 10),
    application("inside", 12, 18),
    application("after", 20, 30),
  ];
  expect(summarizeSource(source, records, 10, 20)).toEqual({
    milliseconds: 6,
    subjects: 1,
    invalid: 0,
  });
});

it("counts overlapping observations once while preserving gaps and malformed payloads", () => {
  const records = [
    application("app", 8, 14),
    application("app", 12, 16),
    application("another", 18, 25),
    { ...application("broken", 10, 20), value: null },
  ];
  const original = structuredClone(records);
  expect(summarizeSource(source, records, 10, 20)).toEqual({
    milliseconds: 8,
    subjects: 2,
    invalid: 1,
  });
  expect(records).toEqual(original);
});
