import { useSearchParams } from "next/navigation";
import { readViewRange } from "@/lib/viewRange";
import { useReplaySelection } from "./useReplaySelection";

export function useLinkedReplaySelection() {
  const params = useSearchParams();
  return useReplaySelection({ range: readViewRange(params), collectorId: params.get("collector") });
}
