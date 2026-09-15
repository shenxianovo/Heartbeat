import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { ReplayLane, TimelineRecord, TrackSummary } from "@/api/types";
import { TimelineViewport } from "@/components/replay/TimelineViewport";

const from = "2026-09-14T00:00:00Z";
const to = "2026-09-15T00:00:00Z";
const track: TrackSummary = {
  id: "track",
  collectorId: "collector",
  collectorKey: "example",
  collectorTarget: "example",
  collectorDisplayName: "Example",
  type: "example.range",
  version: 1,
  timeMode: "range",
  endMode: "explicit",
  createdAt: from,
};
function record(id: string): TimelineRecord {
  return {
    id,
    startedAt: "2026-09-14T12:00:00Z",
    endedAt: "2026-09-14T12:00:01Z",
    observedAt: null,
    receivedAt: to,
    value: { note: id },
  };
}
function lane(records: TimelineRecord[], currentTrack = track): ReplayLane {
  return { track: currentTrack, records, counts: null };
}

describe("timeline record geometry and selection", () => {
  it("does not turn a one-second interval into minutes of observed coverage", () => {
    render(
      <TimelineViewport
        lanes={[lane([record("short")])]}
        from={from}
        to={to}
        zoom={1}
        onSelectPoints={() => {}}
      />,
    );
    const width = parseFloat(screen.getByTitle("时间区间").style.width);
    expect(width).toBeCloseTo(100 / 86_400, 8);
  });

  it("does not keep details from a source excluded by the current selection", () => {
    const view = render(
      <TimelineViewport
        lanes={[lane([record("first")])]}
        from={from}
        to={to}
        zoom={1}
        onSelectPoints={() => {}}
      />,
    );
    fireEvent.click(screen.getByTitle("时间区间"));
    expect(screen.getByText("所选区间")).toBeInTheDocument();
    view.rerender(
      <TimelineViewport
        lanes={[lane([record("second")], { ...track, id: "second-track" })]}
        from={from}
        to={to}
        zoom={1}
        onSelectPoints={() => {}}
      />,
    );
    expect(screen.queryByText("所选区间")).not.toBeInTheDocument();
  });
});
