import { describe, expect, it } from "vitest";

import type { TimelineRecord, TrackSummary } from "@/api/types";
import {
  protocolRecordLabel,
  protocolRecordTitle,
  protocolRecordSummary,
} from "@/components/replay/protocolSummary";

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
    window: { title: "Private draft.md" },
  },
};

describe("application timeline summary", () => {
  it("uses application identity for the overview and keeps the window title auxiliary", () => {
    expect(protocolRecordLabel(track, record)).toBe("Example Editor");
    expect(protocolRecordTitle(track, record)).toBe("Example Editor · Private draft.md");
  });

  it("uses safe generic summaries for malformed values and unknown protocols", () => {
    expect(protocolRecordLabel(track, { ...record, value: { application: 3 } })).toBe("时间区间");
    expect(protocolRecordLabel({ ...track, type: "example.custom" }, record)).toBe("时间区间");
    expect(
      protocolRecordTitle({ ...track, timeMode: "point", type: "example.custom" }, record),
    ).toBe("瞬时记录");
  });
});

it("groups applications by platform identity rather than their display name", () => {
  const first = protocolRecordSummary(track, record).group;
  const second = protocolRecordSummary(track, {
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
  expect(protocolRecordSummary(track, { ...record, value: null }).group).toBeUndefined();
});
