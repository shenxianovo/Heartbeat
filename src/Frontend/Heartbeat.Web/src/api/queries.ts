import { useQuery, useQueryClient, useMutation } from "@tanstack/react-query";
import { api } from "./client";
import type { EntityResponse } from "./types";

export const entityQueryKey = (id: string) => ["entity", id] as const;

export function useEntity(id: string | null | undefined) {
  return useQuery({
    queryKey: id ? entityQueryKey(id) : ["entity", "none"],
    queryFn: () => api.getEntity(id as string),
    enabled: !!id,
    staleTime: 30_000,
    retry: (failureCount, error) =>
      // 4xx 是确定结果（不存在/非法），不重试；只对网络抖动重试一次。
      (error as { status?: number }).status !== undefined && (error as { status: number }).status < 500
        ? false
        : failureCount < 1,
  });
}

export function useSaveEntity() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: {
      segment: string;
      id: string;
      references: Record<string, string | null>;
      properties: Record<string, unknown>;
    }) =>
      args.segment === "observers"
        ? api.saveObserver(args.id, String(args.properties.name))
        : args.segment === "schemas"
          ? api.saveSchema(args.id, { references: args.references, properties: args.properties })
          : args.segment === "observations"
            ? api.saveObservation(args.id, { references: args.references, properties: args.properties })
            : api.saveBusiness(args.segment, args.id, { references: args.references, properties: args.properties }),
    onSuccess: (_data, vars) => {
      void qc.invalidateQueries({ queryKey: entityQueryKey(vars.id) });
    },
  });
}

export type EntityRecord = EntityResponse;
