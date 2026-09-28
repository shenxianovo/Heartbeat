import { rangeToIso, todayRange, toLocalInputValue, type DateRange } from "./dates";

export function recentRange(days: number): DateRange {
  const range = todayRange();
  const start = new Date(range.from);
  start.setDate(start.getDate() - days + 1);
  return { from: toLocalInputValue(start), to: range.to };
}

export function readViewRange(params: { get: (key: string) => string | null }): DateRange | null {
  const from = params.get("from");
  const to = params.get("to");
  if (!from || !to || !rangeToIso({ from, to })) return null;
  return { from: toLocalInputValue(new Date(from)), to: toLocalInputValue(new Date(to)) };
}

export function detailHref(path: string, range: DateRange, collectorId?: string): string {
  const params = new URLSearchParams({ ...rangeToIso(range)! });
  if (collectorId) params.set("collector", collectorId);
  return `${path}?${params}`;
}
