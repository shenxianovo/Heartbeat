import { useMemo, type ReactNode } from "react";
import { Link, useSearch } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import {
  isEntitySchema,
  isInputSet,
  isObservation,
  type EntitySchema,
  type FieldDefinition,
  type InputSet,
  type Version,
} from "@/api/types";
import { Blueprint } from "@/components/entity/Content";
import { Mono } from "@/components/ui";
import { useEntityVersions, useReferrers, useVersion } from "@/hooks/queries";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { describeVersion, useObservationOf, type EntityInfo } from "@/hooks/useEntityInfo";
import { useNow } from "@/hooks/useNow";
import { useTypes } from "@/hooks/useTypes";
import { LOCAL_APPLICATIONS, PRODUCTS } from "@/lib/applications";
import { groupThings, summarize, type Segment } from "@/lib/day";
import { outgoingReferences } from "@/lib/references";
import { differences } from "@/lib/roles";
import { ago, clock, dayRange, dayTitle, duration, parseTime } from "@/lib/time";
import { CATEGORY_NAMES } from "@/lib/versions";
import { cn } from "@/lib/utils";
import { Token } from "../token/Token";
import { Membership, ProductSections } from "./application";

export function PanelBody({ info }: { info: EntityInfo }) {
  return (
    <div className="panel-body grid gap-6">
      {info.role === "thing" && <ThingBody info={info} />}
      {info.role === "experience" && <ExperienceBody info={info} />}
      {info.role === "source" && <SourceBody info={info} />}
      {info.role === "definition" && <DefinitionBody info={info} />}
      {info.role === "evidence" && <EvidenceBody info={info} />}
      <Raw version={info.version} />
    </div>
  );
}

function Section({ title, aside, children }: { title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section>
      <div className="mb-1.5 flex items-baseline gap-2">
        <h3 className="text-fg-2 text-xs font-medium">{title}</h3>
        <span className="flex-1" />
        {aside && <span className="text-fg-3 text-xs">{aside}</span>}
      </div>
      {children}
    </section>
  );
}

function Empty({ children }: { children: ReactNode }) {
  return <p className="text-fg-3 text-[13px]">{children}</p>;
}

/** 面板读的日期：日页面的 date，没有时是今天。 */
function useDateKey(): string {
  const search = useSearch({ strict: false }) as { date?: string };
  const now = useNow(60_000);
  return search.date ?? dayRange(now).key;
}

// —— 事物 ——

function ThingBody({ info }: { info: EntityInfo }) {
  const key = useDateKey();
  const day = useDay(key);
  const catalog = useCatalog();
  const resourceName = info.schema?.content.resourceName;
  const isProduct = resourceName === PRODUCTS;
  // 产品的用量是归到它的各个本机应用的合计。
  const experiences = useMemo(
    () =>
      isProduct && catalog.data
        ? groupThings(day.data?.experiences ?? [], productGroup(catalog.data))
        : (day.data?.experiences ?? []),
    [isProduct, catalog.data, day.data],
  );
  const summary = useMemo(
    () => (day.data ? summarize(experiences, [], day.range.start, day.range.end) : null),
    [day.data, experiences, day.range.start, day.range.end],
  );
  const row = summary?.things.find((r) => r.key === info.version.entityId);
  const seenBy = useMemo(() => {
    const ids = new Set(
      experiences
        .filter((e) => e.things.some((t) => t.version.entityId === info.version.entityId))
        .map((e) => e.observerVersionId),
    );
    return [...ids];
  }, [experiences, info.version.entityId]);

  return (
    <>
      <Section title={dayTitle(day.range.start)} aside={row ? `${row.segments.length} 段` : undefined}>
        {day.loading ? (
          <Empty>读取中</Empty>
        ) : !row ? (
          <Empty>这一天没有出现</Empty>
        ) : (
          <>
            <p className="text-[26px] leading-tight font-medium tabular-nums">{duration(row.total)}</p>
            <DayStrip
              segments={row.segments}
              from={day.range.start}
              to={day.range.end}
              color={info.color ?? "var(--fg-2)"}
            />
            <ul className="mt-2 grid gap-0.5">
              {row.segments
                .slice(-6)
                .reverse()
                .map((segment) => {
                  const experience = experiences.find((e) => e.observationId === segment.observationId);
                  return (
                    <li key={segment.observationId} className="flex items-center gap-2 text-[13px]">
                      {experience?.observation ? (
                        <Token version={experience.observation} size="sm" />
                      ) : (
                        <span className="tabular-nums">{clock(segment.start)}</span>
                      )}
                      {segment.ongoing && <span className="text-accent text-xs">进行中</span>}
                      <span className="flex-1" />
                      <span className="text-fg-3 text-xs tabular-nums">{duration(segment.end - segment.start)}</span>
                    </li>
                  );
                })}
            </ul>
          </>
        )}
      </Section>
      {isProduct ? (
        <ProductSections product={info.version} />
      ) : (
        <>
          {resourceName === LOCAL_APPLICATIONS && <Membership install={info.version} />}
          <Names entityId={info.version.entityId} />
          <Siblings info={info} />
        </>
      )}
      <Section title="内容">
        <Fields version={info.version} schema={info.schema} />
      </Section>
      {seenBy.length > 0 && (
        <Section title="谁看到过它">
          <div className="flex flex-wrap gap-1">
            {seenBy.map((versionId) => (
              <Token key={versionId} versionId={versionId} size="sm" />
            ))}
          </div>
        </Section>
      )}
    </>
  );
}

/** 一天 24 小时的细条，段落画在各自的时间上。 */
export function DayStrip({
  segments,
  from,
  to,
  color,
  compact,
}: {
  segments: readonly Pick<Segment, "start" | "end">[];
  from: number;
  to: number;
  color: string;
  /** 只画细条，不写时刻。 */
  compact?: boolean;
}) {
  const span = to - from;
  return (
    <div className="mt-2">
      <div className={cn("bg-line relative overflow-hidden rounded-full", compact ? "h-1.5" : "h-2")}>
        {segments.map((segment, i) => (
          <span
            key={i}
            className="absolute inset-y-0"
            style={{
              left: `${((segment.start - from) / span) * 100}%`,
              width: `max(1px, ${((segment.end - segment.start) / span) * 100}%)`,
              background: color,
            }}
          />
        ))}
      </div>
      <div className={cn("text-fg-3 mt-0.5 flex justify-between text-[10px] tabular-nums", compact && "hidden")}>
        <span>0</span>
        <span>6</span>
        <span>12</span>
        <span>18</span>
        <span>24</span>
      </div>
    </div>
  );
}

/** 引用它的内容里读到的名字，例如前台时段的 name。 */
function Names({ entityId }: { entityId: string }) {
  const referrers = useReferrers(entityId);
  const pages = referrers.data?.pages ?? [];
  const all = pages.flatMap((page) => page.referrers);
  const names = new Map<string, number>();
  const types = new Map<string, { version: Version; count: number }>();
  for (const { version } of all) {
    const key = version.schemaVersionId ?? version.category;
    types.set(key, { version, count: (types.get(key)?.count ?? 0) + 1 });
    const content = (version.content ?? {}) as Record<string, unknown>;
    const name = typeof content.name === "string" ? content.name : null;
    if (name) names.set(name, (names.get(name) ?? 0) + 1);
  }
  const more = referrers.hasNextPage ? "+" : "";
  if (referrers.isPending) return null;
  return (
    <>
      {names.size > 0 && (
        <Section title="叫过的名字">
          <ul className="flex flex-wrap gap-1.5">
            {[...names.entries()]
              .sort((a, b) => b[1] - a[1])
              .map(([name, count]) => (
                <li key={name} className="bg-panel rounded-md px-2 py-0.5 text-[13px]">
                  {name}
                  <span className="text-fg-3 ml-1.5 text-xs tabular-nums">
                    {count}
                    {more}
                  </span>
                </li>
              ))}
          </ul>
        </Section>
      )}
      <Section title="被引用">
        {all.length === 0 ? (
          <Empty>没有其他实体引用它</Empty>
        ) : (
          <ul className="grid gap-1">
            {[...types.entries()].map(([key, { version, count }]) => (
              <li key={key} className="flex items-center gap-2 text-[13px]">
                <TypeName version={version} />
                <span className="flex-1" />
                <span className="text-fg-3 text-xs tabular-nums">
                  {count}
                  {more} 个版本
                </span>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}

function TypeName({ version }: { version: Version }) {
  const schema = useVersion(version.category === "business" ? version.schemaVersionId : null);
  if (version.category !== "business") return <span>{CATEGORY_NAMES[version.category]}</span>;
  return <span>{schema.data && isEntitySchema(schema.data) ? schema.data.content.displayName : "…"}</span>;
}

/** 同名的其他实体，例如同一个 bundle id 的另一个安装。并排列出，写出它们内容里不同的那一段。 */
function Siblings({ info }: { info: EntityInfo }) {
  const source = useSource();
  const resourceName = info.schema?.content.resourceName;
  const list = useQuery({
    queryKey: ["entities", { resourceName }, "all"],
    queryFn: () => source.listEntities({ resourceName: resourceName!, limit: 1000 }),
    enabled: !!resourceName,
  });
  const group = (list.data?.versions ?? []).filter(
    (v) => describeVersion(v, info.schema, info.role).title === info.title,
  );
  if (group.length < 2) return null;
  const details = differences(group.map((v) => v.content));
  return (
    <Section title={`同名的${info.typeName}`} aside={`${group.length} 个`}>
      <ul className="grid gap-0.5">
        {group.map((version, i) => (
          <li key={version.entityId} className="flex items-center">
            <Token version={version} size="sm" detail={details[i]} />
            {version.entityId === info.version.entityId && <span className="text-fg-3 ml-1 text-xs">· 这个</span>}
          </li>
        ))}
      </ul>
    </Section>
  );
}

// —— 经历 ——

function ExperienceBody({ info }: { info: EntityInfo }) {
  const direct = isObservation(info.version) ? info.version : null;
  const { observation, preview, pending } = useObservationOf(info);
  const contentQuery = useVersion(direct ? direct.content.contentVersionId : null);
  const content = direct ? (contentQuery.data ?? null) : info.version;
  const contentSchema = useVersion(content?.category === "business" ? content.schemaVersionId : null);
  const fields =
    contentSchema.data && isEntitySchema(contentSchema.data) ? contentSchema.data.content.fields : undefined;
  const now = useNow(1000);

  // 进行中的经历：预览接在已保存的版本之后，是较新的陈述。
  const statement = preview?.content ?? observation?.content ?? null;
  const start = parseTime(statement?.startAt);
  const end = parseTime(statement?.endAt);
  const references = content ? outgoingReferences(content, fields) : [];

  return (
    <>
      {statement ? (
        <Section title={start !== null ? dayTitle(start) : "事实时间"}>
          <p className="text-[22px] leading-tight font-medium tabular-nums">
            {start !== null && end !== null && end > start
              ? `${clock(start)} – ${clock(end)}`
              : start !== null || end !== null
                ? clock((start ?? end)!)
                : "时间未知"}
          </p>
          {start !== null && end !== null && end > start && (
            <p className="text-fg-2 mt-0.5 text-[13px]">
              {duration(end - start)}
              {preview && <span className="text-accent"> · 进行中</span>}
            </p>
          )}
        </Section>
      ) : (
        pending && <Empty>读取中</Empty>
      )}

      {content && (
        <Section title={direct ? "观测内容" : "内容"}>
          {direct && (
            <div className="mb-1.5">
              <Token version={content} />
            </div>
          )}
          {references.length > 0 && (
            <div className="mb-1.5 flex flex-wrap items-center gap-1 text-[13px]">
              <span className="text-fg-3">关于</span>
              {references.map((reference) => (
                <Token key={reference.path} versionId={reference.versionId} />
              ))}
            </div>
          )}
          <Fields version={content} schema={null} fields={fields} skipReferences />
        </Section>
      )}

      {preview && !observation && (
        <Section title="由谁记下">
          <div className="flex flex-wrap items-center gap-1.5 text-[13px]">
            <Token versionId={preview.content.observerVersionId} />
            <span className="text-fg-3 text-xs">预览 · {ago(now - Date.parse(preview.updatedAt))}更新，还没有保存</span>
          </div>
        </Section>
      )}

      {observation && (
        <>
          <Section title="由谁记下">
            <div className="flex flex-wrap items-center gap-1.5 text-[13px]">
              <Token versionId={observation.content.observerVersionId} />
              <span className="text-fg-3 text-xs">{ago(now - Date.parse(observation.recordedAt))}保存</span>
            </div>
            <Versions entityId={observation.entityId} />
          </Section>
          {observation.content.inputSetVersionId && (
            <Section title="依据">
              <Token versionId={observation.content.inputSetVersionId} />
              <Members versionId={observation.content.inputSetVersionId} />
            </Section>
          )}
          <ReferrerTokens entityId={observation.entityId} title="对这份观测的陈述" />
          {!direct && (
            <Section title="Observation">
              <Token version={observation} />
            </Section>
          )}
        </>
      )}
    </>
  );
}

function Versions({ entityId }: { entityId: string }) {
  const chain = useEntityVersions(entityId);
  if (!chain.data || chain.data.length < 2) return null;
  return (
    <p className="text-fg-3 mt-1 text-xs">
      更新过 {chain.data.length - 1} 次，共 {chain.data.length} 个版本
    </p>
  );
}

function Members({ versionId }: { versionId: string }) {
  const set = useVersion(versionId);
  if (!set.data || !isInputSet(set.data)) return null;
  return (
    <ul className="border-line mt-1.5 ml-2 grid gap-0.5 border-l pl-2.5">
      {(set.data.content as InputSet).members.map((member) => (
        <li key={member}>
          <Token versionId={member} size="sm" />
        </li>
      ))}
    </ul>
  );
}

function ReferrerTokens({ entityId, title }: { entityId: string; title: string }) {
  const referrers = useReferrers(entityId);
  const all = referrers.data?.pages.flatMap((p) => p.referrers) ?? [];
  if (!all.length) return null;
  return (
    <Section title={title} aside={`${all.length}${referrers.hasNextPage ? "+" : ""}`}>
      <div className="flex flex-wrap gap-1">
        {all.slice(0, 12).map(({ version }) => (
          <Token key={version.versionId} version={version} size="sm" />
        ))}
      </div>
    </Section>
  );
}

// —— 来源 ——

function SourceBody({ info }: { info: EntityInfo }) {
  const key = useDateKey();
  const day = useDay(key);
  const now = useNow(5000);
  const written = (day.data?.experiences ?? []).filter((e) => e.observerEntityId === info.version.entityId);
  const points = (day.data?.points ?? []).find((p) => p.key === info.version.entityId);
  const saved = written.filter((e) => e.observation).length;
  const latest = Math.max(...written.map((e) => e.writtenAt), ...(points?.times ?? []), 0);
  return (
    <>
      <Section title="内容">
        <Fields version={info.version} schema={info.schema} />
      </Section>
      <Section title={dayTitle(day.range.start)}>
        {day.loading ? (
          <Empty>读取中</Empty>
        ) : !written.length && !points ? (
          <Empty>这一天没有写入</Empty>
        ) : (
          <>
            <p className="text-[22px] leading-tight font-medium tabular-nums">
              {saved + (points?.times.length ?? 0)}
              <span className="text-fg-2 ml-1 text-[13px] font-normal">份 Observation</span>
            </p>
            {latest > 0 && <p className="text-fg-3 mt-0.5 text-xs">最近一次 {ago(now - latest)}</p>}
            {written.length > 0 && (
              <DayStrip
                segments={written.map((e) => ({ start: e.start, end: e.end }))}
                from={day.range.start}
                to={day.range.end}
                color="var(--fg-2)"
              />
            )}
          </>
        )}
      </Section>
    </>
  );
}

// —— 定义和依据 ——

function DefinitionBody({ info }: { info: EntityInfo }) {
  const types = useTypes();
  if (!isEntitySchema(info.version)) return null;
  const schema = info.version.content as EntitySchema;
  const edges = types.data?.edges ?? [];
  const name = (key: string) => types.data?.nodes.find((n) => n.key === key)?.name ?? key;
  const out = edges.filter((e) => e.from === schema.resourceName);
  const into = edges.filter((e) => e.to === schema.resourceName);
  return (
    <>
      {schema.description && <p className="text-fg-2 text-[13px]">{schema.description}</p>}
      <Section title="字段">
        <Blueprint fields={schema.fields} />
      </Section>
      {(out.length > 0 || into.length > 0) && (
        <Section title="引用">
          <ul className="grid gap-1 text-[13px]">
            {out.map((e) => (
              <li key={`o${e.to}${e.label}`}>
                <Mono className="text-fg-2">{e.label}</Mono> → {name(e.to)}
              </li>
            ))}
            {into.map((e) => (
              <li key={`i${e.from}${e.label}`}>
                {name(e.from)} <Mono className="text-fg-2">{e.label}</Mono> → 这里
              </li>
            ))}
          </ul>
        </Section>
      )}
      <Link
        to="/entities"
        search={{ type: schema.resourceName }}
        className="text-fg-2 hover:text-fg text-[13px] underline-offset-4 hover:underline"
      >
        查看这个类型的实体 →
      </Link>
    </>
  );
}

function EvidenceBody({ info }: { info: EntityInfo }) {
  return (
    <>
      <Section title="输入">
        <Members versionId={info.version.versionId} />
      </Section>
      <ReferrerTokens entityId={info.version.entityId} title="用到它的 Observation" />
    </>
  );
}

// —— 字段 ——

const COMPACT_ITEMS = 3;

/** 内容的字段表。limit 限制显示的行数，多出的写成“还有 N 个字段”。 */
export function Fields({
  version,
  schema,
  fields: given,
  skipReferences,
  skip,
  limit,
}: {
  version: Version;
  schema: Version<EntitySchema> | null;
  fields?: Record<string, FieldDefinition> | undefined;
  skipReferences?: boolean;
  /** 已经写在别处的字段，例如作为名字的那个。 */
  skip?: string | undefined;
  limit?: number;
}) {
  const fields = given ?? schema?.content.fields ?? {};
  const content = (version.content ?? {}) as Record<string, unknown>;
  const names = [...Object.keys(fields), ...Object.keys(content).filter((k) => !(k in fields))].filter(
    (name) => content[name] !== undefined && name !== skip && !(skipReferences && fields[name]?.type === "reference"),
  );
  if (!names.length) return null;
  const shown = limit === undefined ? names : names.slice(0, limit);
  return (
    <>
      <dl className="grid gap-1.5">
        {shown.map((name) => (
          <div
            key={name}
            className={cn(
              "grid gap-3 text-[13px]",
              limit === undefined
                ? "grid-cols-[minmax(0,7.5rem)_minmax(0,1fr)]"
                : "grid-cols-[minmax(0,5.5rem)_minmax(0,1fr)]",
            )}
          >
            <dt className="text-fg-3 truncate font-mono text-xs leading-5" title={fields[name]?.description}>
              {name}
            </dt>
            <dd className="min-w-0">
              {/* 限制行数时是预览，每个值也写得紧凑些。 */}
              <FieldValue value={content[name]} definition={fields[name]} compact={limit !== undefined} />
            </dd>
          </div>
        ))}
      </dl>
      {shown.length < names.length && (
        <p className="text-fg-3 mt-1.5 text-xs">还有 {names.length - shown.length} 个字段</p>
      )}
    </>
  );
}

/** 字段的值。compact 时数组只列前几项，长文字只写一两行。 */
function FieldValue({
  value,
  definition,
  compact,
}: {
  value: unknown;
  definition: FieldDefinition | undefined;
  compact?: boolean | undefined;
}) {
  if (value === null) return <span className="text-fg-3">null</span>;
  if (definition?.type === "reference" && typeof value === "string") return <Token versionId={value} size="sm" />;
  if (Array.isArray(value)) {
    if (!value.length) return <span className="text-fg-3">空</span>;
    const shown = compact ? value.slice(0, COMPACT_ITEMS) : value;
    return (
      <ul className="grid gap-1">
        {shown.map((item, i) => (
          <li key={i} className="min-w-0">
            <FieldValue value={item} definition={definition?.items} compact={compact} />
          </li>
        ))}
        {shown.length < value.length && <li className="text-fg-3 text-xs">还有 {value.length - shown.length} 项</li>}
      </ul>
    );
  }
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    // 标识线索这类 { scheme, value } 写成一行。
    if (typeof record.scheme === "string" && "value" in record)
      return (
        <span className="block min-w-0">
          <span className="text-fg-3 font-mono text-[11px]">{record.scheme}</span>
          <span className={cn("block font-mono text-xs", compact ? "truncate" : "break-all")}>
            {String(record.value)}
          </span>
        </span>
      );
    return (
      <dl className="grid gap-0.5">
        {Object.entries(record).map(([key, item]) => (
          <div key={key} className="flex min-w-0 gap-2">
            <dt className="text-fg-3 shrink-0 font-mono text-[11px]">{key}</dt>
            <dd className="min-w-0">
              <FieldValue value={item} definition={definition?.fields?.[key]} compact={compact} />
            </dd>
          </div>
        ))}
      </dl>
    );
  }
  if (typeof value === "boolean") return <span>{value ? "是" : "否"}</span>;
  return <span className={cn("break-words", compact && "line-clamp-2")}>{String(value)}</span>;
}

function Raw({ version }: { version: Version }) {
  return (
    <details className="group">
      <summary className="text-fg-3 hover:text-fg cursor-pointer text-xs select-none">原始数据</summary>
      <pre className="bg-panel mt-2 overflow-x-auto rounded-lg p-3 font-mono text-[11px] leading-relaxed">
        {JSON.stringify(version, null, 2)}
      </pre>
    </details>
  );
}
