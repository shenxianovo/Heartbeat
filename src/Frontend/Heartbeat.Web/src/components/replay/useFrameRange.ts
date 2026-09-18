import { useCallback, useEffect, useRef } from "react";
import type { TimeRange } from "./timeRange";

/** Keep only the newest pointer position until the next browser frame. */
export function useFrameRange(onRange: (range: TimeRange) => void) {
  const callback = useRef(onRange);
  useEffect(() => {
    callback.current = onRange;
  }, [onRange]);
  const pending = useRef<TimeRange | null>(null);
  const frame = useRef<number | null>(null);

  const flush = useCallback(() => {
    if (frame.current !== null) cancelAnimationFrame(frame.current);
    frame.current = null;
    const next = pending.current;
    pending.current = null;
    if (next) callback.current(next);
  }, []);
  const cancel = useCallback(() => {
    if (frame.current !== null) cancelAnimationFrame(frame.current);
    frame.current = null;
    pending.current = null;
  }, []);
  const schedule = useCallback(
    (next: TimeRange) => {
      pending.current = next;
      if (frame.current === null) frame.current = requestAnimationFrame(flush);
    },
    [flush],
  );
  useEffect(() => cancel, [cancel]);
  return { schedule, flush, cancel };
}
