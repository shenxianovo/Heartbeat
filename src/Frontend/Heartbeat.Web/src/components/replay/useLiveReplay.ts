import { useEffect, useEffectEvent } from "react";

const LIVE_REPLAY_INTERVAL_MS = 15_000;

/** One external clock drives both the viewport and query refresh. */
export function useLiveReplay(
  enabled: boolean,
  advanceClock: (now: number) => void,
  refresh: () => Promise<void>,
  fetching: boolean,
) {
  const tick = useEffectEvent(() => {
    if (document.visibilityState !== "visible") return;
    advanceClock(Date.now());
    if (!fetching) void refresh();
  });
  useEffect(() => {
    if (!enabled) return;
    const timer = setInterval(tick, LIVE_REPLAY_INTERVAL_MS);
    document.addEventListener("visibilitychange", tick);
    return () => {
      clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [enabled]);
}
