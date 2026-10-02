import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useReplaySelection } from "./useReplaySelection";

const day = { from: "2026-09-18T00:00", to: "2026-09-19T00:00" };
const otherDay = { from: "2026-09-19T00:00", to: "2026-09-20T00:00" };
const detail = {
  trackId: "track",
  from: "2026-09-18T10:00:00Z",
  to: "2026-09-18T11:00:00Z",
  count: 2,
};

describe("useReplaySelection", () => {
  afterEach(() => vi.useRealTimers());

  it("preserves manual browsing across midnight and ignores late initial data", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-18T23:59:50"));
    const { result } = renderHook(() => useReplaySelection());
    vi.setSystemTime(new Date("2026-09-19T00:00:20"));
    act(() => result.current.advanceClock(Date.now()));
    expect(result.current.chosen!.from).toBe("2026-09-19T00:00");
    act(() => result.current.chooseDate(day));
    const initializeOldDay = result.current.establishInitialRange;
    act(() => result.current.setRange(result.current.bounds!));
    act(() => initializeOldDay(new Date("2026-09-18T10:00:00").getTime()));
    expect(result.current.range).toEqual(result.current.bounds);
    vi.setSystemTime(new Date("2026-09-20T00:00:20"));
    act(() => result.current.advanceClock(Date.now()));
    expect(result.current.chosen).toEqual(day);
    act(() => result.current.returnToNow());
    act(() => initializeOldDay(new Date("2026-09-18T10:00:00").getTime()));
    expect(result.current.chosen!.from).toBe("2026-09-20T00:00");
    expect(result.current.following).toBe(true);
  });

  it("clears detail when date or viewport changes", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-18T12:00:00Z"));
    const { result } = renderHook(() => useReplaySelection());

    act(() => result.current.chooseDate(day));
    act(() => result.current.setDetail(detail));
    act(() =>
      result.current.setRange({
        start: result.current.bounds!.start + 60_000,
        end: result.current.bounds!.end,
      }),
    );
    expect(result.current.detail).toBeNull();

    act(() => result.current.setDetail(detail));
    act(() => result.current.chooseDate(otherDay));
    expect(result.current.detail).toBeNull();
    expect(result.current.range!.end - result.current.range!.start).toBe(2 * 60 * 60_000);
    vi.useRealTimers();
  });

  it("steps whole days by one day and custom windows by their own span", () => {
    const { result } = renderHook(() => useReplaySelection({ range: day }));
    act(() => result.current.stepDay(1));
    expect(result.current.chosen).toEqual(otherDay);
    expect(result.current.custom).toBe(false);

    act(() =>
      result.current.chooseDate({ from: "2026-09-12T08:30", to: "2026-09-19T08:30" }, true),
    );
    expect(result.current.custom).toBe(true);
    act(() => result.current.stepDay(-1));
    expect(result.current.chosen).toEqual({ from: "2026-09-05T08:30", to: "2026-09-12T08:30" });

    act(() =>
      result.current.chooseDate({ from: "2026-09-18T20:00", to: "2026-09-19T02:00" }, true),
    );
    act(() => result.current.stepDay(1));
    expect(result.current.chosen).toEqual({ from: "2026-09-19T20:00", to: "2026-09-20T02:00" });
  });
});
