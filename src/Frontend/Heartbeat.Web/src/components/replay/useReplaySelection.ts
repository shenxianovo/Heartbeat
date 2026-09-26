import { useCallback, useMemo, useReducer, useSyncExternalStore } from "react";
import { rangeToIso, todayRange, type DateRange } from "@/lib/dates";
import { clampRange, type TimeRange } from "./timeRange";
import type { PointSelection } from "./TimelineLane";

const FOCUS_RADIUS_MS = 60 * 60_000;

interface Selection {
  chosen: DateRange | null;
  collectorIds: string[] | null;
  viewport: TimeRange | null;
  detail: PointSelection | null;
  mode: "follow" | "initial" | "manual";
  now: number;
}

type Action =
  | { type: "date"; value: DateRange; mode: Selection["mode"]; now: number }
  | { type: "collectors"; value: string[] | null }
  | { type: "viewport"; value: TimeRange }
  | { type: "initial"; from: string; value: TimeRange }
  | { type: "tick"; now: number }
  | { type: "now"; now: number }
  | { type: "detail"; value: PointSelection | null };

function reduceSelection(state: Selection, action: Action): Selection {
  switch (action.type) {
    case "date":
      return {
        ...state,
        chosen: action.value,
        mode: action.mode,
        now: action.now,
        viewport: null,
        detail: null,
      };
    case "collectors":
      return { ...state, collectorIds: action.value, detail: null };
    case "viewport":
      return { ...state, viewport: action.value, mode: "manual", detail: null };
    case "initial":
      return initializeViewport(state, action.from, action.value);
    case "tick":
      return state.mode === "follow"
        ? { ...state, now: action.now, chosen: todayRange(new Date(action.now)) }
        : state;
    case "now":
      return {
        ...state,
        now: action.now,
        chosen: todayRange(new Date(action.now)),
        mode: "follow",
        viewport: null,
        detail: null,
      };
    case "detail":
      return { ...state, detail: action.value };
  }
}

function initializeViewport(state: Selection, from: string, value: TimeRange): Selection {
  return state.mode === "initial" && rangeToIso(state.chosen!)?.from === from
    ? { ...state, viewport: value, mode: "manual" }
    : state;
}

const subscribeHydration = () => () => undefined;

function focusRange(at: number, bounds: TimeRange): TimeRange {
  return clampRange({ start: at - FOCUS_RADIUS_MS, end: at + FOCUS_RADIUS_MS }, bounds);
}

export function useReplaySelection() {
  const hydrated = useSyncExternalStore(
    subscribeHydration,
    () => true,
    () => false,
  );
  const [state, dispatch] = useReducer(reduceSelection, null, (): Selection => ({
    chosen: null,
    collectorIds: null,
    viewport: null,
    detail: null,
    mode: "follow",
    now: Date.now(),
  }));
  const initialRange = useMemo(
    () => (hydrated ? todayRange(new Date(state.now)) : null),
    [hydrated, state.now],
  );
  const chosen = state.chosen ?? initialRange;
  const iso = chosen ? rangeToIso(chosen) : null;
  const from = iso?.from ?? "";
  const to = iso?.to ?? "";
  const bounds = useMemo(
    () => (from && to ? { start: Date.parse(from), end: Date.parse(to) } : null),
    [from, to],
  );
  const following = state.mode === "follow";
  const range = useMemo(() => {
    if (!bounds) return null;
    if (state.viewport) return clampRange(state.viewport, bounds);
    if (following) return focusRange(state.now, bounds);
    return state.mode === "initial" ? focusRange((bounds.start + bounds.end) / 2, bounds) : bounds;
  }, [bounds, state.viewport, state.mode, state.now, following]);

  function setRange(next: TimeRange) {
    if (bounds) dispatch({ type: "viewport", value: clampRange(next, bounds) });
  }
  function pauseFollowing() {
    if (range && state.mode !== "manual") dispatch({ type: "viewport", value: range });
  }
  function chooseDate(value: DateRange, custom = false) {
    const now = Date.now();
    const mode = custom
      ? "manual"
      : value.from === todayRange(new Date(now)).from
        ? "follow"
        : "initial";
    dispatch({ type: "date", value, mode, now });
  }
  const establishInitialRange = useCallback(
    (firstAt: number | null | undefined) => {
      // One-time render adjustment, before children commit. A user-owned viewport
      // is never reinitialized by refreshed data or a late response.
      if (state.mode !== "initial" || firstAt === undefined || !bounds) return;
      dispatch({
        type: "initial",
        from,
        value: focusRange(firstAt ?? (bounds.start + bounds.end) / 2, bounds),
      });
    },
    [bounds, from, state.mode],
  );
  function stepDay(step: number) {
    if (!chosen) return;
    const date = new Date(chosen.from);
    date.setDate(date.getDate() + step);
    chooseDate(todayRange(date));
  }
  return {
    chosen,
    from,
    to,
    bounds,
    range,
    following,
    needsInitialRange: state.mode === "initial",
    advanceClock: (now: number) => dispatch({ type: "tick", now }),
    selectedCollectorIds: state.collectorIds,
    detail: state.detail,
    chooseDate,
    stepDay,
    setRange,
    pauseFollowing,
    establishInitialRange,
    returnToNow: () => dispatch({ type: "now", now: Date.now() }),
    selectCollectors: (value: string[] | null) => dispatch({ type: "collectors", value }),
    setDetail: (value: PointSelection | null) => {
      if (value) pauseFollowing();
      dispatch({ type: "detail", value });
    },
  };
}
