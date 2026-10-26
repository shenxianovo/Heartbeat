// 演示数据源：在浏览器内模拟约 14 个 Observer 今天的数据和持续的写入，接口和 HTTP 实现相同。
// 只用于开发时查看布局和 Playwright，`?source=demo` 开启。

import { registerDemoCategories, type CategoryKey } from "@/lib/categories";
import { dayRange, MINUTE, SECOND } from "@/lib/time";
import { uuidv7 } from "@/lib/uuid";
import { ApiError, type OrisumiSource } from "./client";
import type {
  Category,
  EntitySchema,
  FieldDefinition,
  InputSet,
  Observation,
  ReferenceLocation,
  Referrer,
  Version,
  VersionPage,
} from "./types";

type Mode = "period" | "point";

interface ObserverSpec {
  name: string;
  resourceName: string;
  category: CategoryKey;
  content: string;
  mode: Mode;
  /** 每秒写入次数。 */
  rate: number;
  /** 今天的历史是否密集。 */
  busy: boolean;
}

const SPECS: ObserverSpec[] = [
  spec("前台状态", "foreground-state-observers", "desktop", "前台时段", "period", 0.5, true),
  spec("输入事件", "input-event-observers", "desktop", "输入事件", "point", 1.4, true),
  spec("设备状态", "device-state-observers", "desktop", "设备状态时段", "period", 0.006, true),
  spec("屏幕截图", "screen-capture-observers", "desktop", "屏幕截图", "point", 0.03, false),
  spec("音频", "audio-observers", "desktop", "音频片段", "period", 0, false),
  spec("浏览历史", "browser-history-observers", "web", "浏览记录", "point", 0.12, true),
  spec("助手会话", "assistant-session-observers", "web", "助手会话", "period", 0.05, false),
  spec("播放", "playback-observers", "web", "播放记录", "period", 0, false),
  spec("VRChat", "vrchat-observers", "web", "VRChat 实例", "period", 0, false),
  spec("心率", "heart-rate-observers", "body", "心率读数", "point", 0.2, true),
  spec("步数", "step-count-observers", "body", "步数", "point", 0.02, false),
  spec("睡眠", "sleep-observers", "body", "睡眠时段", "period", 0, false),
  spec("活动推断", "activity-inference-observers", "inference", "活动推断", "period", 0, false),
  spec("用户本人", "people", "you", "陈述", "point", 0.002, false),
];

function spec(
  name: string,
  resourceName: string,
  category: CategoryKey,
  content: string,
  mode: Mode,
  rate: number,
  busy: boolean,
): ObserverSpec {
  return { name, resourceName, category, content, mode, rate, busy };
}

interface Entity {
  versions: string[];
  deleted: boolean;
}

interface Live {
  spec: ObserverSpec;
  observerVersionId: string;
  contentSchemaVersionId: string;
  /** 正在延长的 Observation。 */
  ongoing: string | null;
}

export interface DemoOptions {
  now?: () => number;
  random?: () => number;
}

export function createDemoSource(options: DemoOptions = {}): OrisumiSource {
  const now = options.now ?? Date.now;
  const random = options.random ?? Math.random;
  registerDemoCategories(Object.fromEntries(SPECS.map((s) => [s.resourceName, s.category])));

  const versions = new Map<string, Version>();
  const entities = new Map<string, Entity>();
  const live: Live[] = [];
  let advancedTo = now();

  function put<C>(
    entityId: string,
    category: Category,
    schemaVersionId: string | null,
    content: C,
    at: number,
  ): Version<C> {
    const entity = entities.get(entityId) ?? { versions: [], deleted: false };
    entities.set(entityId, entity);
    const version: Version<C> = {
      entityId,
      versionId: uuidv7(Math.floor(at)),
      previousVersionId: entity.versions.at(-1) ?? null,
      category,
      schemaVersionId,
      recordedAt: new Date(at).toISOString(),
      content,
    };
    entity.versions.push(version.versionId);
    versions.set(version.versionId, version);
    return version;
  }

  function create<C>(category: Category, schemaVersionId: string | null, content: C, at: number): Version<C> {
    return put(uuidv7(Math.floor(at)), category, schemaVersionId, content, at);
  }

  function schema(displayName: string, resourceName: string, fields: Record<string, FieldDefinition>, at: number) {
    const content: EntitySchema = { displayName, description: `${displayName}（演示数据）`, resourceName, fields };
    return create("entity_schema", null, content, at);
  }

  function current(entityId: string): Version | undefined {
    const entity = entities.get(entityId);
    if (!entity || entity.deleted) return undefined;
    return versions.get(entity.versions.at(-1)!);
  }

  function observe(
    state: Live,
    start: number,
    end: number | null,
    at: number,
    extra: Partial<Observation> = {},
  ): Version<Observation> {
    const content = create("business", state.contentSchemaVersionId, { summary: state.spec.content }, at);
    return create<Observation>(
      "observation",
      null,
      {
        observerVersionId: state.observerVersionId,
        contentVersionId: content.versionId,
        inputSetVersionId: null,
        startAt: new Date(start).toISOString(),
        endAt: end === null ? null : new Date(end).toISOString(),
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        ...extra,
      },
      at,
    );
  }

  function extend(observationEntityId: string, end: number, at: number): Version<Observation> {
    const previous = current(observationEntityId) as Version<Observation>;
    return put<Observation>(
      observationEntityId,
      "observation",
      null,
      { ...previous.content, endAt: new Date(end).toISOString() },
      at,
    );
  }

  // 世界：Schema、Observer 实体和今天的历史。
  const t0 = now();
  const { start: dayStart } = dayRange(t0);
  const begin = Math.max(dayStart, t0 - 15 * 60 * MINUTE);
  const created = begin - MINUTE;
  const observerSchemaFields: Record<string, FieldDefinition> = {
    name: { type: "string", required: true, nullable: false },
  };
  const contentFields: Record<string, FieldDefinition> = {
    summary: { type: "string", required: true, nullable: false },
  };

  for (const s of SPECS) {
    const observerSchema = schema(s.name, s.resourceName, observerSchemaFields, created);
    const contentSchema = schema(s.content, `demo-${s.resourceName}-contents`, contentFields, created);
    const observer = create("business", observerSchema.versionId, { name: s.name }, created);
    live.push({
      spec: s,
      observerVersionId: observer.versionId,
      contentSchemaVersionId: contentSchema.versionId,
      ongoing: null,
    });
  }

  for (const state of live) history(state);
  scenario();

  function history(state: Live): void {
    const { spec: s } = state;
    // 推断 Observer 只形成 scenario() 里的那一份推断。
    if (s.category === "inference") return;
    const recent = t0 - 10 * MINUTE;
    let t = begin + random() * 90 * MINUTE;
    while (t < recent) {
      const length = s.busy ? (20 + random() * 120) * MINUTE : (3 + random() * 25) * MINUTE;
      const end = Math.min(recent, t + length);
      observe(state, t, s.mode === "period" ? end : null, end);
      t = end + (s.busy ? random() * 40 * MINUTE : (60 + random() * 180) * MINUTE);
    }
    // 最近 10 分钟按速率补上写入，打开页面时就能看到活动。
    if (s.rate <= 0) return;
    t = recent;
    while (t < t0) {
      t += (-Math.log(1 - random()) / s.rate) * SECOND;
      if (t >= t0) break;
      write(state, t);
    }
  }

  function write(state: Live, at: number): void {
    if (state.spec.mode === "point") {
      observe(state, at, null, at);
      return;
    }
    if (state.ongoing && random() > 0.04) {
      extend(state.ongoing, at, at);
      return;
    }
    state.ongoing = observe(state, at, at, at).entityId;
  }

  /** 一份推断、它的 InputSet，以及用户对它的否定。用来看引用链和“被引用”。 */
  function scenario(): void {
    const foreground = live[0]!;
    const browser = live.find((l) => l.spec.resourceName === "browser-history-observers")!;
    const inference = live.find((l) => l.spec.category === "inference")!;
    const person = live.find((l) => l.spec.category === "you")!;
    const windowEnd = Math.max(begin + 30 * MINUTE, t0 - 3 * 60 * MINUTE);
    const windowStart = Math.max(begin, windowEnd - 2 * 60 * MINUTE);

    const first = observe(foreground, windowStart, windowStart + 20 * MINUTE, windowStart + 20 * MINUTE);
    extend(first.entityId, windowStart + 50 * MINUTE, windowStart + 50 * MINUTE);
    const second = observe(foreground, windowStart + 60 * MINUTE, windowEnd, windowEnd);
    const gone = observe(browser, windowStart + 30 * MINUTE, null, windowStart + 30 * MINUTE);
    const inputs = create<InputSet>(
      "input_set",
      null,
      { members: [first.versionId, second.versionId, gone.versionId] },
      windowEnd + 5 * MINUTE,
    );
    const inferred = observe(inference, windowStart, windowEnd, windowEnd + 5 * MINUTE, {
      inputSetVersionId: inputs.versionId,
    });
    entities.get(gone.entityId)!.deleted = true;

    const statementSchema = schema(
      "陈述",
      "demo-statements",
      {
        target: { type: "reference", required: true, nullable: false },
        statement: { type: "string", required: true, nullable: false },
      },
      created,
    );
    const statement = create(
      "business",
      statementSchema.versionId,
      { target: inferred.versionId, statement: "在开会" },
      windowEnd + 30 * MINUTE,
    );
    create<Observation>(
      "observation",
      null,
      {
        observerVersionId: person.observerVersionId,
        contentVersionId: statement.versionId,
        inputSetVersionId: null,
        startAt: new Date(windowEnd - 60 * MINUTE).toISOString(),
        endAt: new Date(windowEnd).toISOString(),
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      },
      windowEnd + 30 * MINUTE,
    );
  }

  function advance(): void {
    const to = now();
    const dt = (to - advancedTo) / SECOND;
    if (dt <= 0) return;
    for (const state of live) {
      if (state.spec.rate <= 0) continue;
      let t = advancedTo;
      for (;;) {
        t += (-Math.log(1 - random()) / state.spec.rate) * SECOND;
        if (t > to) break;
        write(state, t);
      }
    }
    advancedTo = to;
  }

  function page(all: Version[], limit = 100, after?: string): VersionPage {
    const offset = after ? Number(after) : 0;
    const size = Math.min(Math.max(limit, 1), 1000);
    const slice = all.slice(offset, offset + size);
    return { versions: slice, next: offset + size < all.length ? String(offset + size) : null };
  }

  function readable(versionId: string): Version {
    const version = versions.get(versionId);
    if (!version) throw new ApiError(404, "没有这个版本");
    if (entities.get(version.entityId)?.deleted) throw new ApiError(410, "这个版本已删除");
    return version;
  }

  function references(content: unknown, path: string, out: ReferenceLocation[]): void {
    if (typeof content === "string") {
      if (versions.has(content)) out.push({ path, targetVersionId: content });
    } else if (Array.isArray(content)) {
      content.forEach((item, i) => references(item, `${path}/${i}`, out));
    } else if (content && typeof content === "object") {
      for (const [key, value] of Object.entries(content)) references(value, `${path}/${key}`, out);
    }
  }

  function currentVersions(): Version[] {
    const out: Version[] = [];
    for (const entity of entities.values()) {
      if (!entity.deleted) out.push(versions.get(entity.versions.at(-1)!)!);
    }
    return out;
  }

  const delay = <T>(value: () => T): Promise<T> =>
    new Promise((resolve, reject) => {
      setTimeout(() => {
        try {
          advance();
          resolve(value());
        } catch (error) {
          reject(error);
        }
      }, 30);
    });

  return {
    getVersion: (versionId) => delay(() => readable(versionId)),
    getEntity: (entityId) =>
      delay(() => {
        const entity = entities.get(entityId);
        if (!entity) throw new ApiError(404, "没有这个实体");
        if (entity.deleted) throw new ApiError(410, "这个实体已删除");
        return versions.get(entity.versions.at(-1)!)!;
      }),
    listEntities: (query) =>
      delay(() => {
        const all = currentVersions()
          .filter((v) => !query.category || v.category === query.category)
          .filter((v) => {
            if (!query.resourceName) return true;
            const schemaVersion = v.schemaVersionId ? versions.get(v.schemaVersionId) : undefined;
            return (schemaVersion?.content as EntitySchema | undefined)?.resourceName === query.resourceName;
          })
          .sort((a, b) => a.entityId.localeCompare(b.entityId));
        return page(all, query.limit, query.after);
      }),
    listEntityVersions: (entityId) =>
      delay(() => {
        const entity = entities.get(entityId);
        if (!entity) throw new ApiError(404, "没有这个实体");
        if (entity.deleted) throw new ApiError(410, "这个实体已删除");
        return { versions: entity.versions.map((id) => versions.get(id)!) };
      }),
    listReferrers: (entityId, query) =>
      delay(() => {
        const target = entities.get(entityId);
        if (!target) throw new ApiError(404, "没有这个实体");
        const targets = new Set(target.versions);
        const candidates =
          query.versions === "all"
            ? [...versions.values()].filter((v) => !entities.get(v.entityId)?.deleted)
            : currentVersions();
        const referrers: Referrer[] = [];
        for (const version of candidates.sort((a, b) => a.versionId.localeCompare(b.versionId))) {
          const found: ReferenceLocation[] = [];
          references(version.content, "/content", found);
          const hits = found.filter((r) => targets.has(r.targetVersionId));
          if (hits.length) referrers.push({ version, references: hits });
        }
        const offset = query.after ? Number(query.after) : 0;
        const size = Math.min(Math.max(query.limit ?? 100, 1), 1000);
        return {
          referrers: referrers.slice(offset, offset + size),
          next: offset + size < referrers.length ? String(offset + size) : null,
        };
      }),
    listObservations: (query) =>
      delay(() => {
        const from = Date.parse(query.from);
        const to = Date.parse(query.to);
        const matches = currentVersions()
          .filter((v): v is Version<Observation> => v.category === "observation")
          .filter((v) => {
            const start = v.content.startAt ? Date.parse(v.content.startAt) : null;
            const end = v.content.endAt ? Date.parse(v.content.endAt) : null;
            if (start !== null && end !== null) return start < to && end >= from;
            const point = start ?? end;
            return point !== null && point >= from && point < to;
          })
          .sort((a, b) => {
            const ka = Date.parse(a.content.startAt ?? a.content.endAt!);
            const kb = Date.parse(b.content.startAt ?? b.content.endAt!);
            return ka - kb || a.entityId.localeCompare(b.entityId);
          });
        return page(matches, query.limit, query.after);
      }),
    deleteEntity: (entityId) =>
      delay(() => {
        const entity = entities.get(entityId);
        if (!entity) throw new ApiError(404, "没有这个实体");
        const head = versions.get(entity.versions.at(-1)!)!;
        if (head.category === "entity_schema" && !entity.deleted) {
          const inUse = [...versions.values()].some(
            (v) =>
              v.schemaVersionId && entity.versions.includes(v.schemaVersionId) && !entities.get(v.entityId)?.deleted,
          );
          if (inUse) throw new ApiError(409, "还有业务版本使用这个 EntitySchema", { rule: "HISTORY-9" });
        }
        entity.deleted = true;
      }),
  };
}
