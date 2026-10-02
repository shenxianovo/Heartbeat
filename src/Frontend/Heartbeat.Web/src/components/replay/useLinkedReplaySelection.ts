import { useEffect } from "react";
import { useSearchParams } from "next/navigation";
import { readViewRange, writeViewRange } from "@/lib/viewRange";
import { useReplaySelection } from "./useReplaySelection";

export function useLinkedReplaySelection() {
  const params = useSearchParams();
  const selection = useReplaySelection({ range: readViewRange(params) });
  const from = selection.chosen?.from;
  const to = selection.chosen?.to;
  useEffect(() => {
    if (from && to) writeViewRange({ from, to });
  }, [from, to]);
  return selection;
}
