import { useState, type ReactNode } from "react";
import {
  isEntitySchema,
  isInputSet,
  isObservation,
  type FieldDefinition,
  type InputSet,
  type Observation,
  type Version,
} from "@/api/types";
import { Chip, Mono, SectionTitle } from "@/components/ui";
import { useVersion } from "@/hooks/queries";
import { dateTime, parseTime } from "@/lib/time";
import { VersionChip } from "./VersionChip";

function Rows({ children }: { children: ReactNode }) {
  return <dl className="grid grid-cols-[120px_minmax(0,1fr)] gap-y-2 text-[13px]">{children}</dl>;
}

function Row({ name, children }: { name: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-fg-2 truncate">{name}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}

export function Content({ version }: { version: Version }) {
  if (isObservation(version)) return <ObservationContent version={version} />;
  if (isInputSet(version)) return <InputSetContent version={version} />;
  if (isEntitySchema(version)) {
    const schema = version.content;
    return (
      <Rows>
        <Row name="类型名">{schema.displayName}</Row>
        <Row name="资源名">
          <Mono>{schema.resourceName}</Mono>
        </Row>
        <Row name="说明">{schema.description || <span className="text-fg-3">无</span>}</Row>
        <Row name="字段">
          <Fields fields={schema.fields} />
        </Row>
      </Rows>
    );
  }
  return <BusinessContent version={version} />;
}

function time(value: string | null): ReactNode {
  const parsed = parseTime(value);
  return parsed === null ? <span className="text-fg-3">未知</span> : dateTime(parsed);
}

function ObservationContent({ version }: { version: Version<Observation> }) {
  const content = version.content;
  return (
    <>
      <Rows>
        <Row name="Observer">
          <VersionChip versionId={content.observerVersionId} />
        </Row>
        <Row name="观测内容">
          <VersionChip versionId={content.contentVersionId} strong />
        </Row>
        <Row name="InputSet">
          {content.inputSetVersionId ? (
            <VersionChip versionId={content.inputSetVersionId} />
          ) : (
            <span className="text-fg-3">无</span>
          )}
        </Row>
        <Row name="事实时间">
          <Mono>
            {time(content.startAt)} – {time(content.endAt)}
            {content.timeZone && <span className="text-fg-3"> · {content.timeZone}</span>}
          </Mono>
        </Row>
      </Rows>
      <Trace observation={content} />
    </>
  );
}

/** 引用链（EVIDENCE-7）：观测内容、Observer、InputSet，点开 InputSet 列出每个输入版本。 */
function Trace({ observation }: { observation: Observation }) {
  const inputSet = useVersion(observation.inputSetVersionId);
  const [open, setOpen] = useState(false);
  const members =
    inputSet.data && isInputSet(inputSet.data) ? (inputSet.data as Version<InputSet>).content.members : [];

  return (
    <div className="border-line mt-3.5 border-t pt-3">
      <SectionTitle>引用链</SectionTitle>
      <div className="flex flex-wrap items-center gap-1.5">
        <VersionChip versionId={observation.contentVersionId} strong />
        <span className="text-fg-3 text-xs">←</span>
        <VersionChip versionId={observation.observerVersionId} />
        {observation.inputSetVersionId && (
          <>
            <span className="text-fg-3 text-xs">←</span>
            {inputSet.isError ? (
              <Chip dashed>不可读取</Chip>
            ) : (
              <button
                type="button"
                aria-expanded={open}
                onClick={() => setOpen((value) => !value)}
                className="cursor-pointer"
              >
                <Chip strong={open}>InputSet · {members.length || "…"}</Chip>
              </button>
            )}
          </>
        )}
      </div>
      {open && members.length > 0 && (
        <ul aria-label="InputSet 的输入" className="mt-2 flex flex-wrap gap-1.5">
          {members.map((member) => (
            <li key={member}>
              <VersionChip versionId={member} showNumber />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function InputSetContent({ version }: { version: Version<InputSet> }) {
  return (
    <Rows>
      <Row name="输入">
        <div className="flex flex-wrap gap-1.5">
          {version.content.members.map((member) => (
            <VersionChip key={member} versionId={member} showNumber />
          ))}
        </div>
      </Row>
    </Rows>
  );
}

function BusinessContent({ version }: { version: Version }) {
  const schema = useVersion(version.schemaVersionId);
  const content = (version.content ?? {}) as Record<string, unknown>;
  const fields = schema.data && isEntitySchema(schema.data) ? schema.data.content.fields : {};
  const names = [...Object.keys(fields), ...Object.keys(content).filter((key) => !(key in fields))];
  if (!names.length) return <span className="text-fg-3 text-[13px]">没有字段</span>;
  return (
    <Rows>
      {names.map((name) => (
        <Row key={name} name={name}>
          <Value value={content[name]} definition={fields[name]} />
        </Row>
      ))}
    </Rows>
  );
}

function Value({ value, definition }: { value: unknown; definition: FieldDefinition | undefined }) {
  if (value === undefined) return <span className="text-fg-3">缺省</span>;
  if (value === null) return <span className="text-fg-3">null</span>;
  if (definition?.type === "reference" && typeof value === "string") return <VersionChip versionId={value} />;
  if (typeof value === "object") {
    return (
      <pre className="bg-panel overflow-x-auto rounded-md p-2 font-mono text-xs">{JSON.stringify(value, null, 2)}</pre>
    );
  }
  return <span className="break-words">{String(value)}</span>;
}

function Fields({ fields }: { fields: Record<string, FieldDefinition> }) {
  const entries = Object.entries(fields);
  if (!entries.length) return <span className="text-fg-3">无</span>;
  return (
    <ul className="grid gap-1">
      {entries.map(([name, definition]) => (
        <li key={name} className="flex flex-wrap items-baseline gap-2">
          <Mono className="text-fg">{name}</Mono>
          <Mono className="text-fg-3">
            {definition.type}
            {definition.required ? " · 必填" : ""}
            {definition.nullable ? " · 可空" : ""}
          </Mono>
        </li>
      ))}
    </ul>
  );
}
