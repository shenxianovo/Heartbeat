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

const foregroundTrack: TrackSummary = {
  ...track,
  id: "foreground-track",
  type: "desktop.application.foreground",
};
const statusTrack: TrackSummary = {
  ...track,
  id: "status-track",
  type: "desktop.observation.status",
};

function applicationRecord(id: string, applicationId: string, startedAt: string): TimelineRecord {
  return {
    ...record(id),
    startedAt,
    endedAt: new Date(Date.parse(startedAt) + 60_000).toISOString(),
    value: {
      device_id: "device",
      application: { platform: "macos", id_kind: "bundle_id", id: applicationId },
    },
  };
}

function statusRecord(
  id: string,
  capability: "application" | "window_title" | "input",
  state: "available" | "permission_required" | "unavailable",
): TimelineRecord {
  return {
    ...record(id),
    value: { device_id: "device", capability, state, reason: "accessibility" },
  };
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
    const width = parseFloat(screen.getByRole("button", { name: "时间区间" }).style.width);
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
    fireEvent.click(screen.getByRole("button", { name: "时间区间" }));
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

describe("timeline presentation projection", () => {
  it("shows only applications overlapping the viewport and preserves expansion", () => {
    const finder = applicationRecord("finder", "Finder", "2026-09-14T02:00:00Z");
    const code = applicationRecord("code", "Code", "2026-09-14T14:00:00Z");
    const props = {
      lanes: [lane([finder, code], foregroundTrack)],
      bounds: { start: Date.parse(from), end: Date.parse(to) },
      overviewLanes: [] as ReplayLane[],
      densityStatus: null,
      onRange: () => {},
      onSelectPoints: () => {},
    };
    const view = render(
      <TimelineViewport {...props} range={{ start: Date.parse(from), end: Date.parse(to) }} />,
    );

    fireEvent.click(screen.getByRole("button", { name: "展开 Example 的应用" }));
    expect(screen.getByText("Finder", { selector: ".application-sublane strong" })).toBeVisible();
    expect(screen.getByText("Code", { selector: ".application-sublane strong" })).toBeVisible();

    view.rerender(
      <TimelineViewport
        {...props}
        range={{
          start: Date.parse("2026-09-14T01:00:00Z"),
          end: Date.parse("2026-09-14T03:00:00Z"),
        }}
      />,
    );
    expect(screen.getByText("Finder", { selector: ".application-sublane strong" })).toBeVisible();
    expect(screen.queryByText("Code", { selector: ".application-sublane strong" })).toBeNull();
  });

  it("keeps observation status as its own lane and range record", () => {
    render(
      <TimelineViewport
        lanes={[
          lane([], foregroundTrack),
          lane([statusRecord("permission", "application", "permission_required")], statusTrack),
        ]}
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={() => {}}
        onSelectPoints={() => {}}
      />,
    );

    expect(screen.queryByText("前台应用", { selector: ".timeline-lane-label strong" })).toBeNull();
    expect(screen.getByText("观测状态", { selector: ".timeline-lane-label strong" })).toBeVisible();
    const warning = screen.getByRole("button", { name: "应用观测 · 缺少权限 · accessibility" });
    expect(warning).toHaveClass("timeline-tone-attention");
    expect(screen.getByRole("heading", { name: /\u8bb0录/ })).toHaveTextContent("1");

    fireEvent.click(warning);
    expect(screen.getByRole("region", { name: "所选记录详情" })).toHaveTextContent(
      "应用观测 · 缺少权限",
    );
  });

  it("shows an available status only on its own lane", () => {
    render(
      <TimelineViewport
        lanes={[
          lane([], foregroundTrack),
          lane([statusRecord("available", "application", "available")], statusTrack),
        ]}
        bounds={{ start: Date.parse(from), end: Date.parse(to) }}
        range={{ start: Date.parse(from), end: Date.parse(to) }}
        overviewLanes={[]}
        densityStatus={null}
        onRange={() => {}}
        onSelectPoints={() => {}}
      />,
    );

    expect(screen.getByLabelText("Example")).toBeVisible();
    expect(screen.queryByText("前台应用", { selector: ".timeline-lane-label strong" })).toBeNull();
    expect(screen.getByText("观测状态", { selector: ".timeline-lane-label strong" })).toBeVisible();
    expect(screen.getByRole("button", { name: "应用观测 · 可用 · accessibility" })).toBeVisible();
  });
});

describe("timeline wheel interaction", () => {
  it("pans with a horizontal trackpad gesture and keeps the time span", () => {
    const onRange = vi.fn();
    const bounds = { start: Date.parse(from), end: Date.parse(to) };
    const range = { start: bounds.start + 3_600_000, end: bounds.start + 13 * 3_600_000 };
    render(
      <TimelineViewport
        lanes={[lane([record("short")])]}
        bounds={bounds}
        range={range}
        overviewLanes={[]}
        densityStatus={null}
        onRange={onRange}
        onSelectPoints={() => {}}
      />,
    );

    const plot = document.querySelector<HTMLElement>("[data-time-plot]")!;
    const wheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      deltaX: 100,
      deltaY: 4,
    });
    plot.dispatchEvent(wheel);

    expect(wheel.defaultPrevented).toBe(true);
    expect(onRange).toHaveBeenCalledWith({
      start: range.start + (range.end - range.start) * 0.1,
      end: range.end + (range.end - range.start) * 0.1,
    });
  });

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
