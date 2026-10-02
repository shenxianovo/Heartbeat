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

export function writeViewRange(range: DateRange) {
  const iso = rangeToIso(range);
  if (!iso) return;
  const url = new URL(window.location.href);
  if (url.searchParams.get("from") === iso.from && url.searchParams.get("to") === iso.to) return;
  url.searchParams.set("from", iso.from);
  url.searchParams.set("to", iso.to);
  window.history.replaceState(null, "", `${url.pathname}?${url.searchParams}`);
}
