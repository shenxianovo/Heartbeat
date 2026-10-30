import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { management, settled, type Action, type Host, type Instance, type Operation } from "@/api/management";

export function useHosts() {
  return useQuery({
    queryKey: ["hosts"],
    queryFn: async () => (await management<{ hosts: Host[] }>("/hosts")).hosts,
    refetchInterval: 5000,
  });
}

export function useInstances() {
  return useQuery({
    queryKey: ["instances"],
    queryFn: async () => (await management<{ instances: Instance[] }>("/observer-instances")).instances,
    refetchInterval: 5000,
  });
}

/** 修改托管方和实例。成功后重新读取两张列表。 */
export function useManage() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ path, method, body }: { path: string; method: string; body?: unknown }) =>
      management<unknown>(path, method, body),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["hosts"] });
      void client.invalidateQueries({ queryKey: ["instances"] });
    },
  });
}

/** 在线操作：发起后每秒读取一次，直到有了结果。读不到时按结果未确认处理。 */
export function useOperation(hostId: string) {
  const client = useQueryClient();
  const [started, setStarted] = useState<Operation>();
  const [error, setError] = useState<string>();
  const progress = useQuery({
    queryKey: ["operation", started?.operationId],
    enabled: !!started,
    queryFn: () => management<Operation>(`/hosts/${hostId}/operations/${started!.operationId}`),
    refetchInterval: (query) => (query.state.error || settled(query.state.data) ? false : 1000),
    retry: false,
  });
  const lost = progress.isError ? ({ ...started!, state: "unconfirmed" } as Operation) : undefined;
  const operation = lost ?? progress.data ?? started;

  async function start(action: Action, target: { entityId?: string; packageId?: string; newInstance?: boolean } = {}) {
    setError(undefined);
    try {
      setStarted(await management<Operation>(`/hosts/${hostId}/operations`, "POST", { action, ...target }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    }
  }

  const done = !!operation && settled(operation);
  const finished = done && operation.action !== "catalog" ? operation.operationId + operation.state : null;
  useEffect(() => {
    if (finished) void client.invalidateQueries({ queryKey: ["instances"] });
  }, [finished, client]);
  return { operation, error, start, running: !!operation && !done, reset: () => setStarted(undefined) };
}
