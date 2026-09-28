import { useQueries } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import { fetchAllRecords } from "@/api/client";
import { useTracksQuery } from "@/api/queries";
import { rangeToIso, type DateRange } from "@/lib/dates";
import { overviewSources, summarizeSource } from "./model";

export function useOverviewData(range: DateRange) {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const catalog = useTracksQuery(owner, token, 60000);
  const sources = overviewSources(catalog.data?.tracks ?? []);
  const supported = sources.filter((source) => source.track);
  const iso = rangeToIso(range)!;
  const queries = useQueries({
    queries: supported.map((source) => ({
      queryKey: ["overview", owner, source.track!.id, iso.from, iso.to],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        fetchAllRecords(token, { trackId: source.track!.id, ...iso }, signal),
      enabled: Boolean(token),
      refetchInterval: 60000,
    })),
  });
  const summaries = supported.map((source, index) => ({
    ...source,
    ...summarizeSource(
      source,
      queries[index]!.data?.records ?? [],
      Date.parse(iso.from),
      Date.parse(iso.to),
    ),
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
