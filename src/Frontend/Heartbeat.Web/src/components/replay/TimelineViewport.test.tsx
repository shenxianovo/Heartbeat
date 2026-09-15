import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

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
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={() => {}}
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
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={() => {}}
        onSelectPoints={() => {}}
      />,
    );
    fireEvent.click(screen.getByTitle("时间区间"));
    expect(screen.getByText("所选区间")).toBeInTheDocument();
    view.rerender(
      <TimelineViewport
        lanes={[lane([record("second")], { ...track, id: "second-track" })]}
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={() => {}}
        onSelectPoints={() => {}}
      />,
    );
    expect(screen.queryByText("所选区间")).not.toBeInTheDocument();
  });
});

describe("timeline wheel interaction", () => {
  it("leaves an unmodified vertical wheel event to the internal scroll container", () => {
    const onRange = vi.fn();
    render(
      <TimelineViewport
        lanes={[lane([record("short")])]}
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={onRange}
        onSelectPoints={() => {}}
      />,
    );

    const plot = document.querySelector<HTMLElement>("[data-time-plot]");
    expect(plot).not.toBeNull();
    const wheel = new WheelEvent("wheel", { bubbles: true, cancelable: true, deltaY: 120 });
    plot!.dispatchEvent(wheel);

    expect(wheel.defaultPrevented).toBe(false);
    expect(onRange).not.toHaveBeenCalled();
  });

  it("zooms and consumes the wheel event while Control or Command is held", () => {
    const onRange = vi.fn();
    render(
      <TimelineViewport
        lanes={[lane([record("short")])]}
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={onRange}
        onSelectPoints={() => {}}
      />,
    );

    const plot = document.querySelector<HTMLElement>("[data-time-plot]");
    expect(plot).not.toBeNull();
    for (const modifier of [{ ctrlKey: true }, { metaKey: true }]) {
      const wheel = new WheelEvent("wheel", {
        bubbles: true,
        cancelable: true,
        clientX: 50,
        deltaY: -120,
        ...modifier,
      });
      plot!.dispatchEvent(wheel);
      expect(wheel.defaultPrevented).toBe(true);
    }

    expect(onRange).toHaveBeenCalledTimes(2);
  });
});
