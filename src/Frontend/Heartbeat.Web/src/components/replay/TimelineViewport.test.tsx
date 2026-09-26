import { act, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ReplayLane, TimelineRecord, TrackSummary } from "@/api/types";
import type { TimeRange } from "@/components/replay/timeRange";
import { TimelineViewport } from "@/components/replay/TimelineViewport";
import { ActivityOverview } from "@/components/replay/ActivityOverview";

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

const day = { start: Date.parse(from), end: Date.parse(to) };

/** The viewport with everything a test does not care about already filled in. */
function viewport(props: {
  lanes: ReplayLane[];
  range?: TimeRange;
  onRange?: (range: TimeRange) => void;
}) {
  return (
    <TimelineViewport
      lanes={props.lanes}
      bounds={day}
      range={props.range ?? day}
      overviewLanes={[]}
      densityStatus={null}
      onRange={props.onRange ?? (() => {})}
      onSelectPoints={() => {}}
    />
  );
}

function renderViewport(props: Parameters<typeof viewport>[0]) {
  return render(viewport(props));
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
  it("does not keep details from a source excluded by the current selection", () => {
    const view = renderViewport({ lanes: [lane([record("first")])] });
    fireEvent.keyDown(screen.getByRole("button", { name: /当前 时间区间/ }), { key: "Enter" });
    expect(screen.getByText("所选区间")).toBeInTheDocument();
    view.rerender(
      viewport({ lanes: [lane([record("second")], { ...track, id: "second-track" })] }),
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
    renderViewport({
      lanes: [
        lane([], foregroundTrack),
        lane([statusRecord("permission", "application", "permission_required")], statusTrack),
      ],
    });

    expect(screen.queryByText("前台应用", { selector: ".timeline-lane-label strong" })).toBeNull();
    expect(screen.getByText("观测状态", { selector: ".timeline-lane-label strong" })).toBeVisible();
    const warning = screen.getByRole("button", {
      name: /当前 应用观测 · 缺少权限 · accessibility/,
    });
    expect(screen.getByRole("heading", { name: /\u8bb0录/ })).toHaveTextContent("1");

    fireEvent.keyDown(warning, { key: "Enter" });
    expect(screen.getByRole("region", { name: "所选记录详情" })).toHaveTextContent(
      "应用观测 · 缺少权限",
    );
  });

  it("shows an available status only on its own lane", () => {
    renderViewport({
      lanes: [
        lane([], foregroundTrack),
        lane([statusRecord("available", "application", "available")], statusTrack),
      ],
    });

    expect(screen.getByLabelText("Example")).toBeVisible();
    expect(screen.queryByText("前台应用", { selector: ".timeline-lane-label strong" })).toBeNull();
    expect(screen.getByText("观测状态", { selector: ".timeline-lane-label strong" })).toBeVisible();
    expect(
      screen.getByRole("button", { name: /当前 应用观测 · 可用 · accessibility/ }),
    ).toBeVisible();
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

describe("continuous range dragging", () => {
  function frameQueue() {
    const callbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      callbacks.push(callback);
      return callbacks.length;
    });
    vi.spyOn(window, "cancelAnimationFrame").mockImplementation(() => {});
    Object.defineProperty(HTMLElement.prototype, "setPointerCapture", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
      left: 0,
      right: 1000,
      top: 0,
      bottom: 100,
      width: 1000,
      height: 100,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    });
    return () => act(() => callbacks.splice(0).forEach((callback) => callback(0)));
  }

  it("uses the latest main-lane pointer position once per frame", () => {
    const nextFrame = frameQueue();
    const onRange = vi.fn();
    const range = { start: day.start + 3_600_000, end: day.start + 13 * 3_600_000 };
    renderViewport({ lanes: [lane([record("short")])], range, onRange });
    const plot = document.querySelector<HTMLElement>("[data-time-plot]")!;
    const timeline = document.querySelector<HTMLElement>(".swimlane-timeline")!;
    fireEvent.pointerDown(plot, { button: 0, clientX: 500, pointerId: 1 });
    fireEvent.pointerMove(timeline, { clientX: 480, pointerId: 1 });
    fireEvent.pointerMove(timeline, { clientX: 450, pointerId: 1 });
    expect(onRange).not.toHaveBeenCalled();
    nextFrame();
    expect(onRange).toHaveBeenCalledTimes(1);
    expect(onRange).toHaveBeenCalledWith({
      start: range.start + (range.end - range.start) * 0.05,
      end: range.end + (range.end - range.start) * 0.05,
    });
  });

  it("flushes the final overview position when the pointer is released", () => {
    frameQueue();
    const onRange = vi.fn();
    const bounds = { start: Date.parse(from), end: Date.parse(to) };
    const range = { start: bounds.start + 3_600_000, end: bounds.start + 13 * 3_600_000 };
    render(<ActivityOverview lanes={[]} bounds={bounds} range={range} onRange={onRange} />);
    const overview = screen.getByTestId("activity-overview");
    fireEvent.pointerDown(overview, { button: 0, clientX: 500, pointerId: 1 });
    fireEvent.pointerMove(overview, { clientX: 480, pointerId: 1 });
    fireEvent.pointerMove(overview, { clientX: 450, pointerId: 1 });
    expect(onRange).not.toHaveBeenCalled();
    fireEvent.pointerUp(overview, { clientX: 450, pointerId: 1 });
    expect(onRange).toHaveBeenCalledTimes(1);
  });
});
