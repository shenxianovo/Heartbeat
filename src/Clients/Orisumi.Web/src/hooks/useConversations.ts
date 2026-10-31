import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { BinaryValue, Observation, Version } from "@/api/types";
import {
  groupSessions,
  resolvePointer,
  toInteraction,
  type InteractionContent,
  type SessionContent,
  type SessionMeta,
} from "@/lib/conversations";
import { dayOf, dayRange } from "@/lib/time";
import { DAY_INTERVAL } from "./useDay";
import { useNow } from "./useNow";
import { pool, readVersion } from "./useTypes";

export const INTERACTIONS = "assistant-interactions";
const TITLES = "assistant-session-titles";
const SUBAGENTS = "assistant-subagents";

/** 引用这个会话的某种实体中，最后写入的一个。 */
async function latestAbout(source: OrisumiSource, sessionEntityId: string, resourceName: string) {
  const page = await source.listReferrers(sessionEntityId, {
    resourceName,
    path: "/content/sessionVersionId",
    order: "desc",
    limit: 1,
  });
  return page.referrers[0]?.version ?? null;
}

/**
 * 一天的助手会话：按事实时间读出这一天的交互 Observation，读出每次交互和它的会话，再按会话分组。
 * 版本不会改变，读过的都在缓存里，所以今天每 5 秒刷新时只读新的交互。
 */
export function useConversations(key: string) {
  const source = useSource();
  const client = useQueryClient();
  const now = useNow(60_000);
  const range = dayOf(key) ?? dayRange(now);
  const today = range.key === dayRange(now).key;

  const observations = useQuery({
    queryKey: ["observations", "day", range.key, INTERACTIONS],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({
          from: range.from,
          to: range.to,
          resourceName: INTERACTIONS,
          limit: 1000,
          ...(after ? { after } : {}),
        }),
      ).catch((error: unknown) => {
        // 资源名还没有登记时按没有数据处理。
        if (error instanceof ApiError && error.status === 404) return [];
        throw error;
      }) as Promise<Version<Observation>[]>,
    refetchInterval: today ? DAY_INTERVAL : false,
  });

  const groups = useQuery({
    queryKey: ["conversations", range.key, observations.dataUpdatedAt],
    queryFn: async () => {
      const read = (id: string) => readVersion(client, source, id);
      const list = observations.data!;
      const contents = await pool(list, 16, (o) => read(o.content.contentVersionId));
      const interactions = list.flatMap((observation, index) => {
        const content = contents[index];
        return content ? [toInteraction(observation, content as Version<InteractionContent>)] : [];
      });
      const sessionIds = new Set(interactions.map((i) => i.version.content.sessionVersionId));
      const sessions = new Map<string, Version<SessionContent> | null>();
      await pool([...sessionIds], 8, async (id) => {
        sessions.set(id, (await read(id)) as Version<SessionContent> | null);
      });
      const entityIds = new Set([...sessions.values()].flatMap((session) => (session ? [session.entityId] : [])));
      const metas = new Map<string, SessionMeta>();
      await pool([...entityIds], 8, async (entityId) => {
        const meta = await client.fetchQuery({
          queryKey: ["session-meta", entityId],
          staleTime: 30_000,
          queryFn: async (): Promise<SessionMeta> => {
            const [title, subagent] = await Promise.all([
              latestAbout(source, entityId, TITLES),
              latestAbout(source, entityId, SUBAGENTS),
            ]);
            const relation = subagent?.content as
              { parentSessionVersionId: string; agentName: string | null } | undefined;
            const parent = relation ? await read(relation.parentSessionVersionId) : null;
            return {
              title: (title?.content as { title: string } | undefined)?.title ?? null,
              parentEntityId: parent?.entityId ?? null,
              agentName: relation?.agentName ?? null,
            };
          },
        });
        metas.set(entityId, meta);
      });
      return groupSessions(interactions, sessions, metas);
    },
    enabled: !!observations.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  return {
    range,
    today,
    groups: groups.data,
    loading: observations.isPending || (!!observations.data && groups.isPending),
    error: observations.error ?? groups.error,
  };
}

/** 一份原始记录解析后的 JSON。摘要确定了字节，所以读到后一直缓存。 */
export function useRawEntry(entry: BinaryValue | undefined, enabled: boolean) {
  const source = useSource();
  return useQuery({
    queryKey: ["raw-entry", entry?.digest],
    queryFn: async () => JSON.parse(await (await source.getContent(entry!.digest)).text()) as unknown,
    enabled: enabled && !!entry,
    staleTime: Infinity,
  });
}

/** 片段在原始记录中的值。 */
export function usePartValue(
  content: InteractionContent,
  part: { entryIndex: number; pointer: string },
  enabled: boolean,
) {
  const raw = useRawEntry(content.sourceEntries[part.entryIndex], enabled);
  return { ...raw, value: raw.data === undefined ? undefined : resolvePointer(raw.data, part.pointer) };
}
