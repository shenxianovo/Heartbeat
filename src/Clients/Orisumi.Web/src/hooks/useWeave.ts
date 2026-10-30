import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import type { OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import {
  isEntitySchema,
  isInputSet,
  isObservation,
  type EntitySchema,
  type Observation,
  type Referrer,
  type Version,
} from "@/api/types";
import { outgoingReferences, referenceLabel } from "@/lib/references";
import { roleOf, type RoleIndex } from "@/lib/roles";
import { parseTime } from "@/lib/time";
import {
  collapse,
  grow,
  INPUT,
  link,
  linked,
  nextFromBundle,
  payloadKey,
  plan,
  REVEAL,
  reveal,
  SOURCE,
  start,
  unlink,
  type ItemNode,
  type Neighbor,
  type Payload,
  type Resolved,
  type Side,
  type Weave,
} from "@/lib/weave";
import { describeVersion } from "./useEntityInfo";
import { pool, readVersion, useTypes } from "./useTypes";

interface Context {
  client: QueryClient;
  source: OrisumiSource;
  index: RoleIndex;
}

/** 引用一个实体的全部版本和引用位置。几十秒内重复展开时用缓存。 */
function readReferrers({ client, source }: Context, entityId: string): Promise<Referrer[]> {
  return client.fetchQuery({
    queryKey: ["entity", entityId, "referrers", "every"],
    staleTime: 30_000,
    queryFn: async () => {
      const found: Referrer[] = [];
      let after: string | undefined;
      do {
        const page = await source.listReferrers(entityId, { limit: 1000, ...(after ? { after } : {}) });
        found.push(...page.referrers);
        after = page.next ?? undefined;
      } while (after);
      return found;
    },
  });
}

const read = ({ client, source }: Context, versionId: string | null) =>
  versionId ? readVersion(client, source, versionId) : Promise.resolve(null);

async function schemaOf(context: Context, version: Version) {
  if (version.category !== "business") return null;
  const schema = await read(context, version.schemaVersionId);
  return schema && isEntitySchema(schema) ? (schema as Version<EntitySchema>) : null;
}

/** 版本的角色和类型名。 */
async function typeOf(context: Context, version: Version) {
  const schema = await schemaOf(context, version);
  const role = roleOf(version, schema?.content.resourceName, context.index);
  return { schema, role, typeName: describeVersion(version, schema, role).typeName };
}

/** Observation 按事实时间，其余按保存时间。只用来排序。 */
function timeOf(version: Version): number {
  const recorded = parseTime(version.recordedAt) ?? 0;
  if (!isObservation(version)) return recorded;
  return parseTime(version.content.startAt) ?? parseTime(version.content.endAt) ?? recorded;
}

/** 以这个版本为观测内容的 Observation，取最近保存的一份。 */
async function observationOf(context: Context, content: Version) {
  const referrers = await readReferrers(context, content.entityId);
  const found = referrers
    .filter((r) => isObservation(r.version) && r.references.some((l) => l.path.endsWith("contentVersionId")))
    .map((r) => r.version as Version<Observation>)
    .sort((a, b) => b.recordedAt.localeCompare(a.recordedAt));
  return found[0] ?? null;
}

/**
 * 一个版本在织图里画成什么。观测内容是经历的 Observation 并进经历；其余 Observation 是结，带着它的 InputSet。
 * 经历找到记下它的 Observation 一起画。InputSet 和定义不单独成节点，返回 null。
 * parent 是正在展开的节点：关于它的另一份陈述画成结，不并进它自己。
 */
async function resolve(context: Context, version: Version, parent: string | null): Promise<Payload | null> {
  if (isObservation(version)) {
    const content = await read(context, version.content.contentVersionId);
    if (content && content.entityId !== parent && (await typeOf(context, content)).role === "experience")
      return { kind: "entity", version: content, observation: version };
    const inputSet = await read(context, version.content.inputSetVersionId);
    return { kind: "knot", observation: version, inputSet: inputSet && isInputSet(inputSet) ? inputSet : null };
  }
  if (version.category !== "business") return null;
  const { role } = await typeOf(context, version);
  return { kind: "entity", version, observation: role === "experience" ? await observationOf(context, version) : null };
}

/**
 * 一个节点的邻居，还没解析成节点。
 * - 实体：业务字段引用的（右）；记下它的 Observation 的 Observer 和输入（左）；引用它的业务实体（左）；
 *   以它为内容的其他陈述（左）；它写下的 Observation（右）；以它为输入的 Observation（右）。
 * - 结：内容（右）；输入（左）；以这份 Observation 为输入的 Observation（右）。Observer 写在结上，由 toggleSource 长出。
 */
async function neighborsOf(context: Context, node: ItemNode) {
  const candidates: { version: Version; side: Side; label: string }[] = [];
  let unreadable = 0;
  const add = (version: Version | null, side: Side, label: string) => {
    if (version) candidates.push({ version, side, label });
    else unreadable += 1;
  };

  async function inputs(observation: Version<Observation>) {
    const inputSet = await read(context, observation.content.inputSetVersionId);
    if (inputSet && isInputSet(inputSet)) {
      const members = await pool(inputSet.content.members, 8, (versionId) => read(context, versionId));
      members.forEach((member) => add(member, "left", INPUT));
    }
  }

  async function incoming(entityId: string, merged: string | null) {
    for (const referrer of await readReferrers(context, entityId)) {
      const { version } = referrer;
      const paths = referrer.references.map((location) => location.path);
      if (version.category === "business") add(version, "left", paths.map(referenceLabel).join("、"));
      else if (isObservation(version)) {
        if (version.entityId === merged) continue;
        if (paths.some((path) => path.endsWith("observerVersionId"))) add(version, "right", SOURCE);
        else if (paths.some((path) => path.endsWith("contentVersionId"))) add(version, "left", "内容");
      } else if (isInputSet(version)) {
        // 以它为输入的 InputSet，再找到用这个 InputSet 的 Observation。
        for (const user of await readReferrers(context, version.entityId))
          if (isObservation(user.version)) add(user.version, "right", INPUT);
      }
    }
  }

  const { payload } = node;
  if (payload.kind === "entity") {
    const { version, observation } = payload;
    const schema = await schemaOf(context, version);
    const references = outgoingReferences(version, schema?.content.fields);
    const targets = await pool(references, 8, (reference) => read(context, reference.versionId));
    targets.forEach((target, i) => add(target, "right", referenceLabel(references[i]!.path)));
    if (observation) {
      add(await read(context, observation.content.observerVersionId), "left", SOURCE);
      await inputs(observation);
      await incoming(observation.entityId, null);
    }
    await incoming(version.entityId, observation?.entityId ?? null);
  } else {
    add(await read(context, payload.observation.content.contentVersionId), "right", "内容");
    await inputs(payload.observation);
    await incoming(payload.observation.entityId, null);
  }

  const typed = await pool(candidates, 8, (candidate) => typeOf(context, candidate.version));
  const neighbors: Neighbor[] = [];
  candidates.forEach((candidate, i) => {
    const { role, typeName } = typed[i]!;
    if (role === "definition") return;
    neighbors.push({ ...candidate, typeName, time: timeOf(candidate.version) });
  });
  return { neighbors, unreadable };
}

async function resolveAll(context: Context, neighbors: readonly Neighbor[], parent: string): Promise<Resolved[]> {
  const payloads = await pool(neighbors, 8, (neighbor) => resolve(context, neighbor.version, parent));
  return neighbors.flatMap((neighbor, i) =>
    payloads[i] ? [{ payload: payloads[i], side: neighbor.side, label: neighbor.label }] : [],
  );
}

/**
 * 织图的状态：从 rootId 出发，toggle 展开或收起一个节点，open 从线束里再放出一些。
 * fresh 是刚长出来的节点；message 写最近一次做了什么。
 */
export function useWeave(rootId: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const [weave, setWeave] = useState<Weave | null>(null);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState<string | null>(null);

  // 异步展开时读最新的图和类型索引。类型索引每分钟刷新，刷新不重置已经长出来的图。
  const current = useRef<Weave | null>(null);
  const indexRef = useRef<RoleIndex | undefined>(undefined);
  useEffect(() => {
    current.current = weave;
    indexRef.current = types.data;
  });
  const context = useCallback((): Context | null => {
    const index = indexRef.current;
    return index ? { client, source, index } : null;
  }, [client, source]);

  // 起点换了时，画布按起点重建（调用方用 key），这里只读一次。
  const ready = !!types.data;
  useEffect(() => {
    if (!ready || !types.data) return;
    const ctx: Context = { client, source, index: types.data };
    let cancelled = false;
    (async () => {
      const payload = await resolve(ctx, await source.getEntity(rootId), null);
      if (cancelled) return;
      if (!payload) {
        setError("InputSet 和定义跟着用到它们的观测出现，换一个实体作起点");
        return;
      }
      const weave = start(payload);
      setWeave(weave);
      setFresh(new Set(weave.nodes.map((node) => node.key)));
      setMessage("点一个节点，从两边长出和它有关的实体；再点一次收起");
    })().catch((failure: unknown) => {
      if (!cancelled) setError(failure instanceof Error ? failure.message : String(failure));
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rootId, ready, client, source]);

  function settle(before: Weave, next: Weave) {
    const keys = new Set(before.nodes.map((node) => node.key));
    const added = next.nodes.filter((node) => !keys.has(node.key));
    setWeave(next);
    setFresh(new Set(added.map((node) => node.key)));
    return added;
  }

  const toggle = useCallback(
    async (key: string) => {
      const now = current.current;
      const node = now?.nodes.find((item) => item.key === key);
      const ctx = context();
      if (!now || !node || node.kind === "bundle" || !ctx) return;
      if (node.expanded) {
        settle(now, collapse(now, key));
        setMessage("已收起");
        return;
      }
      setBusy(key);
      try {
        const { neighbors, unreadable } = await neighborsOf(ctx, node);
        const { direct, bundles } = plan(neighbors);
        const resolved = await resolveAll(ctx, direct, key);
        const latest = current.current!;
        const added = settle(latest, grow(latest, key, resolved, bundles));
        const grown = added.filter((item) => item.kind !== "bundle").length;
        const bundled = bundles.reduce((sum, bundle) => sum + bundle.members.length, 0);
        setMessage(
          [
            grown || bundled ? `长出 ${grown} 个` : "没有更多相关的实体",
            bundled ? `另有 ${bundled} 个收在线束里` : "",
            unreadable ? `${unreadable} 个引用的目标读不到` : "",
          ]
            .filter(Boolean)
            .join("，"),
        );
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  const open = useCallback(
    async (key: string) => {
      const now = current.current;
      const ctx = context();
      const bundle = now?.nodes.find((item) => item.key === key);
      if (!now || !ctx || bundle?.kind !== "bundle") return;
      setBusy(key);
      try {
        const resolved = await resolveAll(ctx, nextFromBundle(now, key), bundle.parent);
        const latest = current.current!;
        settle(latest, reveal(latest, key, resolved));
        const rest = Math.max(0, bundle.members.length - REVEAL);
        setMessage(rest > 0 ? `线束里还有 ${rest} 个` : "线束放完了");
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  /** 从结上长出它的 Observer，已经连着时收起。 */
  const toggleSource = useCallback(
    async (key: string) => {
      const now = current.current;
      const node = now?.nodes.find((item) => item.key === key);
      const ctx = context();
      if (!now || !ctx || node?.kind !== "knot" || node.payload.kind !== "knot") return;
      setBusy(key);
      try {
        const observer = await read(ctx, node.payload.observation.content.observerVersionId);
        const payload = observer ? await resolve(ctx, observer, key) : null;
        const latest = current.current!;
        if (!payload) setMessage("读不到这份陈述的 Observer");
        else if (node.parent === payloadKey(payload)) setMessage("这个结就是从它的来源长出来的");
        else if (linked(latest, key, payloadKey(payload))) {
          settle(latest, unlink(latest, key, payloadKey(payload)));
          setMessage("已收起来源");
        } else {
          settle(latest, link(latest, key, [{ payload, side: "left", label: SOURCE }]));
          setMessage("长出来源");
        }
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  return {
    weave,
    fresh,
    busy,
    message,
    error: error ?? (types.error as Error | null)?.message ?? null,
    toggle,
    toggleSource,
    open,
  };
}
