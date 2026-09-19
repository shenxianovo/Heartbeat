import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

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
  it("clears detail when date, collector selection, or viewport changes", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-18T12:00:00Z"));
    const { result } = renderHook(() => useReplaySelection());

    act(() => result.current.chooseDate(day));
    act(() => result.current.setDetail(detail));
    act(() => result.current.selectCollectors(["collector"]));
    expect(result.current.detail).toBeNull();

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
    expect(result.current.range).toEqual(result.current.bounds);
    vi.useRealTimers();
  });
});
