import { useMemo, useReducer, useSyncExternalStore } from "react";
import { rangeToIso, todayRange, type DateRange } from "@/lib/dates";
import { clampRange, type TimeRange } from "./timeRange";
import type { PointSelection } from "./TimelineLane";

interface Selection {
  chosen: DateRange | null;
  collectorIds: string[] | null;
  viewport: TimeRange | null;
  detail: PointSelection | null;
}

type Action =
  | { type: "date"; value: DateRange }
  | { type: "collectors"; value: string[] | null }
  | { type: "viewport"; value: TimeRange }
  | { type: "detail"; value: PointSelection | null };

function reduceSelection(state: Selection, action: Action): Selection {
  switch (action.type) {
    case "date":
      return { ...state, chosen: action.value, viewport: null, detail: null };
    case "collectors":
      return { ...state, collectorIds: action.value, detail: null };
    case "viewport":
      return { ...state, viewport: action.value, detail: null };
    case "detail":
      return { ...state, detail: action.value };
  }
}

const subscribeHydration = () => () => undefined;

export function useReplaySelection() {
  const hydrated = useSyncExternalStore(
    subscribeHydration,
    () => true,
    () => false,
  );
  const initialRange = useMemo(() => (hydrated ? todayRange() : null), [hydrated]);
  const [state, dispatch] = useReducer(reduceSelection, {
    chosen: null,
    collectorIds: null,
    viewport: null,
    detail: null,
  });
  const chosen = state.chosen ?? initialRange;
  const iso = chosen ? rangeToIso(chosen) : null;
  const from = iso?.from ?? "";
  const to = iso?.to ?? "";
  const bounds = useMemo(
    () => (from && to ? { start: Date.parse(from), end: Date.parse(to) } : null),
    [from, to],
  );
  const range = useMemo(
    () => bounds && (state.viewport ? clampRange(state.viewport, bounds) : bounds),
    [bounds, state.viewport],
  );
  function setRange(next: TimeRange) {
    if (!bounds || !range) return;
    const value = clampRange(next, bounds);
    if (value.start !== range.start || value.end !== range.end)
      dispatch({ type: "viewport", value });
    else if (state.detail) dispatch({ type: "detail", value: null });
  }
  function chooseDate(value: DateRange) {
    dispatch({ type: "date", value });
  }
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
    selectedCollectorIds: state.collectorIds,
    detail: state.detail,
    chooseDate,
    stepDay,
    setRange,
    selectCollectors: (value: string[] | null) => dispatch({ type: "collectors", value }),
    setDetail: (value: PointSelection | null) => dispatch({ type: "detail", value }),
  };
}
