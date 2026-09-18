import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import type { TimelineRecord, TrackSummary } from "@/api/types";
import { CanvasRanges } from "@/components/replay/CanvasRanges";
import { layoutRanges, rowGeometry, rowTop } from "@/components/replay/rangeLayout";
import { TooltipLayer } from "@/components/ui/Tooltip";

const from = Date.parse("2026-09-14T00:00:00Z");
const to = Date.parse("2026-09-15T00:00:00Z");
const WIDTH = 1000;
/** Three minutes apart, one minute long: neighbours land in their own pixel band. */
const PITCH = 180_000;
// Enough bars to count as a dense lane, while the last one still fits inside the day.
const BARS = 400;

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
  createdAt: new Date(from).toISOString(),
};

function records(count: number): TimelineRecord[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `dense-${index}`,
    startedAt: new Date(from + index * PITCH).toISOString(),
    endedAt: new Date(from + index * PITCH + 60_000).toISOString(),
    observedAt: null,
    receivedAt: new Date(to).toISOString(),
    value: { note: index },
  }));
}

/** jsdom has no drawing surface, so the calls are recorded instead of the pixels. */
function stubSurface() {
  const context = {
    setTransform: vi.fn(),
    clearRect: vi.fn(),
    fillRect: vi.fn(),
    strokeRect: vi.fn(),
    setLineDash: vi.fn(),
    fillStyle: "",
    strokeStyle: "",
    globalAlpha: 1,
    lineWidth: 1,
  };
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(
    context as unknown as CanvasRenderingContext2D,
  );
  vi.spyOn(HTMLCanvasElement.prototype, "getBoundingClientRect").mockReturnValue(
    new DOMRect(0, 0, WIDTH, rowGeometry.pitch + rowGeometry.bottom),
  );
  return context;
}

/** Centre of a bar's pixel band, matching the geometry the surface draws with. */
function bandCentre(index: number) {
  return { clientX: (index * PITCH * WIDTH) / (to - from) + 1, clientY: rowTop(0) + 1 };
}

function surface(count: number, onSelect = vi.fn(), selectedId: string | null = null) {
  const layout = layoutRanges(records(count), from, to);
  render(
    <TooltipLayer>
      <CanvasRanges
        track={track}
        items={layout.items}
        range={{ start: from, end: to }}
        selectedId={selectedId}
        onSelect={onSelect}
      />
    </TooltipLayer>,
  );
  return { canvas: screen.getByRole("button"), onSelect };
}

it("draws every observation on one surface", () => {
  const context = stubSurface();
  const { canvas } = surface(BARS);
  expect(canvas.dataset.segments).toBe(String(BARS));
  expect(context.fillRect).toHaveBeenCalledTimes(BARS);
});

it("selects the observation under the pointer", () => {
  stubSurface();
  const { canvas, onSelect } = surface(BARS);
  fireEvent.click(canvas, bandCentre(7));
  expect(onSelect).toHaveBeenCalledWith("dense-7");
});

it("ignores a click that lands between the bars", () => {
  stubSurface();
  const { canvas, onSelect } = surface(BARS);
  fireEvent.click(canvas, { clientX: WIDTH - 1, clientY: rowTop(0) + 1 });
  expect(onSelect).not.toHaveBeenCalled();
});

it("browses and selects observations from the keyboard", () => {
  stubSurface();
  const { canvas, onSelect } = surface(BARS);
  canvas.focus();
  fireEvent.keyDown(canvas, { key: "ArrowRight" });
  fireEvent.keyDown(canvas, { key: "ArrowRight" });
  fireEvent.keyDown(canvas, { key: "Enter" });
  expect(onSelect).toHaveBeenCalledWith("dense-2");
  expect(canvas).toHaveAttribute("aria-label", expect.stringContaining(`${BARS} 条区间`));
});

it("reads the hovered observation from the protocol registry", () => {
  stubSurface();
  const { canvas } = surface(BARS);
  fireEvent.pointerMove(canvas, { ...bandCentre(3), pointerType: "mouse" });
  expect(screen.getByText("时间区间")).toBeInTheDocument();
});
