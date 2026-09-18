import { describe, expect, it } from "vitest";

import type { TimelineRecord, TrackSummary } from "@/api/types";
import { describeRecord } from "@/components/records/renderers/registry";

const track: TrackSummary = {
  id: "track",
  collectorId: "collector",
  collectorKey: "heartbeat.collector.desktop.macos",
  collectorTarget: "device",
  collectorDisplayName: "Mac",
  type: "desktop.application.foreground",
  version: 1,
  timeMode: "range",
  endMode: "explicit",
  createdAt: "2026-09-14T00:00:00Z",
};

const record: TimelineRecord = {
  id: "record",
  startedAt: "2026-09-14T08:00:00Z",
  endedAt: "2026-09-14T08:05:00Z",
  observedAt: null,
  receivedAt: "2026-09-14T08:05:01Z",
  value: {
    device_id: "device",
    application: {
      platform: "macos",
      id_kind: "bundle_id",
      id: "com.example.Editor",
      display_name: "Example Editor",
    },
  },
};

const windowTrack: TrackSummary = { ...track, type: "desktop.window.foreground" };
const windowRecord: TimelineRecord = {
  ...record,
  value: { device_id: "device", window: { title: "Private draft.md" } },
};

describe("application timeline summary", () => {
  it("names the application by its identity and the window by its title", () => {
    expect(describeRecord(track, record).label).toBe("Example Editor");
    expect(describeRecord(windowTrack, windowRecord).label).toBe("Private draft.md");
  });

  it("keeps the window title out of the application lane", () => {
    expect(describeRecord(windowTrack, record).label).toBe("时间区间");
    expect(describeRecord(windowTrack, windowRecord).group).toBeUndefined();
  });

  it("uses safe generic summaries for malformed values and unknown protocols", () => {
    expect(describeRecord(track, { ...record, value: { application: 3 } }).label).toBe("时间区间");
    expect(describeRecord({ ...track, type: "example.custom" }, record).label).toBe("时间区间");
    expect(
      describeRecord({ ...track, timeMode: "point", type: "example.custom" }, record).label,
    ).toBe("瞬时记录");
  });
});

it("groups applications by platform identity rather than their display name", () => {
  const first = describeRecord(track, record).group;
  const second = describeRecord(track, {
    ...record,
    value: {
      ...(record.value as object),
      device_id: "device",
      application: {
        platform: "macos",
        id_kind: "bundle_id",
        id: "com.example.Other",
        display_name: "Example Editor",
      },
    },
  }).group;
  expect(first?.label).toBe(second?.label);
  expect(first?.id).not.toBe(second?.id);
  expect(describeRecord(track, { ...record, value: null }).group).toBeUndefined();
});
