import { useState, type ComponentType, type ReactNode } from "react";
import { Braces, Hash, Link2, List, ToggleLeft, Type } from "lucide-react";
import {
  isEntitySchema,
  isInputSet,
  isObservation,
  type FieldDefinition,
  type InputSet,
  type Observation,
  type Version,
} from "@/api/types";
import { Chip, Mono } from "@/components/ui";
import { useVersion } from "@/hooks/queries";
import { clock, dateTime, DAY, HOUR, MINUTE, parseTime } from "@/lib/time";
import { cn } from "@/lib/utils";
import { VersionChip } from "./VersionChip";

function Tiles({ children }: { children: ReactNode }) {
  return <dl className="grid gap-2 sm:grid-cols-2">{children}</dl>;
}

/** 一个字段一格：上面是字段名，下面是值。长的值占满一行。 */
function Tile({
  name,
  hint,
  wide,
  children,
}: {
  name: string;
  hint?: string | undefined;
  wide?: boolean;
  children: ReactNode;
}) {
  return (
    <div className={cn("bg-panel/55 min-w-0 rounded-xl px-3 py-2.5", wide && "sm:col-span-2")}>
      <dt className="text-fg-3 truncate text-xs" title={hint}>
        {name}
        {hint && <span className="text-fg-3/80"> · {hint}</span>}
      </dt>
      <dd className="mt-1 min-w-0 text-[14px]">{children}</dd>
    </div>
  );
}

export function Content({ version }: { version: Version }) {
  if (isObservation(version)) return <ObservationMembers version={version} />;
  if (isInputSet(version)) return <InputSetContent version={version} />;
  if (isEntitySchema(version)) {
    const schema = version.content;
    return (
      <div className="grid gap-3">
        <Tiles>
          <Tile name="资源名">
            <Mono>{schema.resourceName}</Mono>
          </Tile>
          <Tile name="字段">{Object.keys(schema.fields).length} 个</Tile>
          <Tile name="说明" wide>
            {schema.description || <span className="text-fg-3">无</span>}
          </Tile>
        </Tiles>
        <Blueprint fields={schema.fields} />
      </div>
    );
  }
  return <BusinessContent version={version} />;
}

function ObservationMembers({ version }: { version: Version<Observation> }) {
  const content = version.content;
  return (
    <Tiles>
      <Tile name="观测内容">
        <VersionChip versionId={content.contentVersionId} strong />
      </Tile>
      <Tile name="Observer">
        <VersionChip versionId={content.observerVersionId} />
      </Tile>
      <Tile name="事实时间" wide>
        <DayTrack startAt={content.startAt} endAt={content.endAt} timeZone={content.timeZone} />
      </Tile>
      {content.inputSetVersionId && (
        <Tile name="InputSet" wide>
          <Inputs versionId={content.inputSetVersionId} />
        </Tile>
      )}
    </Tiles>
  );
}

/** 事实时间画在它所在那天的 24 小时上。只知道一个边界时画一个点。 */
function DayTrack({
  startAt,
  endAt,
  timeZone,
}: {
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
}) {
  const start = parseTime(startAt);
  const end = parseTime(endAt);
  const anchor = start ?? end;
  if (anchor === null) return <span className="text-fg-3">未知</span>;
  const date = new Date(anchor);
  const day = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  const at = (t: number) => `${Math.min(100, Math.max(0, ((t - day) / DAY) * 100))}%`;
  const span = start !== null && end !== null;

  return (
    <div>
      <p className="flex flex-wrap items-baseline gap-x-2">
        <Mono className="text-fg text-[13px]">
          {start === null ? "?" : dateTime(start)} – {end === null ? "?" : span ? clock(end, true) : dateTime(end)}
        </Mono>
        {span && <span className="text-fg-2 text-xs">{duration(end - start)}</span>}
        {timeZone && <span className="text-fg-3 text-xs">{timeZone}</span>}
      </p>
      <div aria-hidden="true" className="relative mt-2.5 h-2 rounded-full bg-[var(--line)]">
        {[6, 12, 18].map((hour) => (
          <span key={hour} className="bg-line-2 absolute top-0 h-2 w-px" style={{ left: `${(hour / 24) * 100}%` }} />
        ))}
        {span ? (
          <span
            className="bg-fg-2 absolute top-0 h-2 min-w-[3px] rounded-full"
            style={{ left: at(start), width: `calc(${at(end)} - ${at(start)})` }}
          />
        ) : (
          <span
            className="bg-fg-2 ring-card absolute -top-0.5 size-3 -translate-x-1/2 rounded-full ring-2"
            style={{ left: at(anchor) }}
          />
        )}
      </div>
      <div aria-hidden="true" className="text-fg-3 mt-1 grid grid-cols-4 text-[11px] tabular-nums">
        <span>0</span>
        <span>6</span>
        <span>12</span>
        <span>18</span>
      </div>
    </div>
  );
}

function duration(ms: number): string {
  if (ms < MINUTE) return `${Math.round(ms / 1000)} 秒`;
  if (ms < HOUR) return `${Math.round(ms / MINUTE)} 分钟`;
  const hours = Math.floor(ms / HOUR);
  const minutes = Math.round((ms % HOUR) / MINUTE);
  return minutes ? `${hours} 小时 ${minutes} 分` : `${hours} 小时`;
}

/** InputSet 的输入：点开列出每个输入版本。 */
function Inputs({ versionId }: { versionId: string }) {
  const inputSet = useVersion(versionId);
  const [open, setOpen] = useState(false);
  const members =
    inputSet.data && isInputSet(inputSet.data) ? (inputSet.data as Version<InputSet>).content.members : [];
  if (inputSet.isError) return <Chip dashed>不可读取</Chip>;
  return (
    <>
      <button type="button" aria-expanded={open} onClick={() => setOpen((value) => !value)} className="cursor-pointer">
        <Chip strong={open}>InputSet · {members.length || "…"}</Chip>
      </button>
      {open && members.length > 0 && (
        <ul aria-label="InputSet 的输入" className="mt-2 flex flex-wrap gap-1.5">
          {members.map((member) => (
            <li key={member}>
              <VersionChip versionId={member} showNumber />
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function InputSetContent({ version }: { version: Version<InputSet> }) {
  return (
    <Tiles>
      <Tile name={`输入 · ${version.content.members.length}`} wide>
        <div className="flex flex-wrap gap-1.5">
          {version.content.members.map((member) => (
            <VersionChip key={member} versionId={member} showNumber />
          ))}
        </div>
      </Tile>
    </Tiles>
  );
}

function BusinessContent({ version }: { version: Version }) {
  const schema = useVersion(version.schemaVersionId);
  const content = (version.content ?? {}) as Record<string, unknown>;
  const fields = schema.data && isEntitySchema(schema.data) ? schema.data.content.fields : {};
  const names = [...Object.keys(fields), ...Object.keys(content).filter((key) => !(key in fields))];
  if (!names.length) return <span className="text-fg-3 text-[13px]">没有字段</span>;
  return (
    <Tiles>
      {names.map((name) => {
        const value = content[name];
        const wide = (value !== null && typeof value === "object") || (typeof value === "string" && value.length > 40);
        return (
          <Tile key={name} name={name} hint={fields[name]?.description || undefined} wide={wide}>
            <Value value={value} definition={fields[name]} />
          </Tile>
        );
      })}
    </Tiles>
  );
}

function Value({ value, definition }: { value: unknown; definition: FieldDefinition | undefined }) {
  if (value === undefined) return <span className="text-fg-3">缺省</span>;
  if (value === null) return <span className="text-fg-3">null</span>;
  if (definition?.type === "reference" && typeof value === "string") return <VersionChip versionId={value} />;
  if (typeof value === "boolean") return <span className={value ? "text-fg" : "text-fg-2"}>{value ? "是" : "否"}</span>;
  if (typeof value === "number")
    return <span className="text-[17px] font-medium tabular-nums">{value.toLocaleString()}</span>;
  if (typeof value === "object") {
    return (
      <pre className="bg-card overflow-x-auto rounded-lg p-2 font-mono text-xs">{JSON.stringify(value, null, 2)}</pre>
    );
  }
  return <span className="break-words">{String(value)}</span>;
}

const TYPE_ICONS: Record<FieldDefinition["type"], ComponentType<{ className?: string }>> = {
  string: Type,
  boolean: ToggleLeft,
  integer: Hash,
  number: Hash,
  reference: Link2,
  object: Braces,
  array: List,
};

/** 字段蓝图：每个字段的名称、类型、是否必填和说明，对象和数组的内部字段缩进列出。 */
export function Blueprint({ fields, compact }: { fields: Record<string, FieldDefinition>; compact?: boolean }) {
  const entries = Object.entries(fields);
  if (!entries.length) return <p className="text-fg-3 text-[13px]">没有字段</p>;
  if (compact) {
    return (
      <ul aria-label="字段" className="flex flex-wrap gap-1.5">
        {entries.map(([name, definition]) => {
          const Icon = TYPE_ICONS[definition.type];
          return (
            <li key={name} title={definition.description}>
              <Chip>
                <Icon className={cn("size-3", definition.type === "reference" && "text-fg")} />
                <span className="font-mono">{name}</span>
                {definition.required && <span className="text-fg-3">*</span>}
              </Chip>
            </li>
          );
        })}
      </ul>
    );
  }
  return (
    <ul aria-label="字段" className="border-line divide-line divide-y rounded-xl border">
      {entries.map(([name, definition]) => (
        <FieldRow key={name} name={name} definition={definition} depth={0} />
      ))}
    </ul>
  );
}

function FieldRow({ name, definition, depth }: { name: string; definition: FieldDefinition; depth: number }) {
  const Icon = TYPE_ICONS[definition.type];
  const inner = definition.fields ?? definition.items?.fields;
  return (
    <li>
      <div className="flex items-start gap-2.5 px-3 py-2" style={{ paddingLeft: 12 + depth * 20 }}>
        <span
          className={cn(
            "bg-panel mt-0.5 grid size-6 shrink-0 place-items-center rounded-md",
            definition.type === "reference" ? "text-fg" : "text-fg-3",
          )}
        >
          <Icon className="size-3.5" />
        </span>
        <div className="min-w-0 flex-1">
          <p className="flex flex-wrap items-baseline gap-x-2">
            <Mono className="text-fg text-[13px]">{name}</Mono>
            <Mono className="text-fg-3">
              {definition.type}
              {definition.type === "array" && definition.items ? `<${definition.items.type}>` : ""}
            </Mono>
            {definition.required && <span className="text-fg-2 text-xs">必填</span>}
            {definition.nullable && <span className="text-fg-3 text-xs">可空</span>}
          </p>
          {definition.description && <p className="text-fg-2 text-xs">{definition.description}</p>}
        </div>
      </div>
      {inner && (
        <ul className="divide-line divide-y">
          {Object.entries(inner).map(([child, childDefinition]) => (
            <FieldRow key={child} name={child} definition={childDefinition} depth={depth + 1} />
          ))}
        </ul>
      )}
    </li>
  );
}
