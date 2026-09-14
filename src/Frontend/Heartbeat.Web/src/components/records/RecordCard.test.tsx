import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import type { TimelineRecord, TrackReference } from "@/api/types";
import { RecordCard } from "@/components/records/RecordCard";

const record: TimelineRecord = {
  id: "019e0000-0000-7000-8000-000000000003",
  startedAt: "2026-09-12T10:00:00Z",
  endedAt: null,
  observedAt: null,
  receivedAt: "2026-09-12T10:00:05Z",
  value: { sample: true },
};

const rangeTrack: TrackReference = {
  id: "019e0000-0000-7000-8000-000000000002",
  collectorId: "019e0000-0000-7000-8000-000000000001",
  type: "custom.sample",
  version: 1,
  timeMode: "range",
  endMode: "next_record",
};

describe("RecordCard", () => {
  it("does not describe a range without endedAt as a point", () => {
    render(<RecordCard record={record} track={rangeTrack} />);

    expect(screen.getAllByText("未提供结束时间")).toHaveLength(2);
    expect(screen.queryByText("时间点")).not.toBeInTheDocument();
  });

  it("reveals metadata and raw JSON on demand", async () => {
    const user = userEvent.setup();
    render(<RecordCard record={record} track={rangeTrack} />);

    expect(screen.queryByText("Record ID")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "查看详情" }));
    expect(screen.getByText("Record ID")).toBeVisible();
    expect(screen.getByText(record.id)).toBeVisible();
    expect(screen.getByText("原始 JSON")).toBeVisible();
    expect(screen.getByRole("button", { name: "收起详情" })).toHaveAttribute(
      "aria-expanded",
      "true",
    );
  });
});
