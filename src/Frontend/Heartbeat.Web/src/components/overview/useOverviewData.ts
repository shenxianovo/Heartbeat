import { useQueries, useQuery } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import { fetchAllRecords, fetchObjects, fetchTracks } from "@/api/client";
import { rangeToIso, type DateRange } from "@/lib/dates";
import { overviewSources, summarizeSource } from "./model";

export function useOverviewData(range: DateRange) {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const catalog = useQuery({
    queryKey: ["owner", owner, "objects"],
    queryFn: ({ signal }) => fetchObjects(token, signal),
    enabled: Boolean(token),
    refetchInterval: 60000,
  });
  const sources = overviewSources(catalog.data?.objects ?? []);
  const iso = rangeToIso(range)!;
  const summarySources = sources.filter((source) => source.kind !== "vrchat");
  const queries = useQueries({
    queries: summarySources.map((source) => ({
      queryKey: ["overview", owner, source.id, iso.from, iso.to],
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        if (source.kind !== "desktop") return { records: [], summaryAvailable: false };
        const scope = { objectId: source.id };
        const { tracks } = await fetchTracks(token, signal, scope);
        const type = "desktop.application.foreground";
        const applicable = tracks.filter((track) => track.type === type && track.version === 1);
        const pages = await Promise.all(
          applicable.map((track) =>
            fetchAllRecords(token, { trackId: track.id, ...iso, ...scope }, signal),
          ),
        );
        return {
          records: pages.flatMap((page) => page.records),
          summaryAvailable: applicable.length > 0,
        };
      },
      enabled: Boolean(token),
      refetchInterval: 60000,
    })),
  });
  const summaries = summarySources.map((source, index) => ({
    ...source,
    ...summarizeSource(
      source,
      queries[index]!.data?.records ?? [],
      Date.parse(iso.from),
      Date.parse(iso.to),
    ),
    summaryAvailable: queries[index]!.data?.summaryAvailable ?? false,
    pending: queries[index]!.isPending,
    failed: queries[index]!.isError,
  }));
  return {
    catalog,
    sources,
    summaries,
    fetching: catalog.isFetching || queries.some((query) => query.isFetching),
    refresh: () => {
      void catalog.refetch();
      queries.forEach((query) => void query.refetch());
    },
  };
}
