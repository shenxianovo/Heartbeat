import { useEffect, useState, useSyncExternalStore } from "react";
import { useQuery } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation, Version } from "@/api/types";
import { ActivityStore } from "@/lib/activity";
import { resolveObserver } from "@/lib/observers";
import { dayRange, liveRange, SECOND } from "@/lib/time";

export const TODAY_INTERVAL = 60 * SECOND;
export const LIVE_INTERVAL = 2 * SECOND;

/** 轮询今天和“现在”的 Observation，交给 ActivityStore 汇总。 */
export function useActivity(now: number) {
  const source = useSource();
  const [store] = useState(() => new ActivityStore((versionId) => resolveObserver(source, versionId)));
  const day = dayRange(now);

  const today = useQuery({
    queryKey: ["observations", "today", day.key],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({ from: day.from, to: day.to, limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Version<Observation>[]>,
    refetchInterval: TODAY_INTERVAL,
  });

  const live = useQuery({
    queryKey: ["observations", "live"],
    queryFn: () => {
      const range = liveRange(Date.now());
      return readAllPages((after) =>
        source.listObservations({ ...range, limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Version<Observation>[]>;
    },
    refetchInterval: LIVE_INTERVAL,
  });

  useEffect(() => {
    if (today.data) store.ingestToday(today.data, Date.now());
  }, [today.data, store]);

  useEffect(() => {
    if (live.data) store.ingestLive(live.data, Date.now());
  }, [live.data, store]);

  const revision = useSyncExternalStore(store.subscribe, store.getRevision);

  return {
    store,
    revision,
    day,
    loading: today.isPending,
    error: today.error ?? live.error,
  };
}
