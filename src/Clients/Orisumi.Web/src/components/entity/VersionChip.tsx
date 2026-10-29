import { Link } from "@tanstack/react-router";
import { isEntitySchema, isObservation, type Version } from "@/api/types";
import { Chip, Dot } from "@/components/ui";
import { useEntityVersions, useVersion } from "@/hooks/queries";
import { tintOf } from "@/lib/categories";
import { UNREADABLE } from "@/lib/observers";
import { clock, parseTime } from "@/lib/time";
import { CATEGORY_NAMES, versionLabel, versionNumber } from "@/lib/versions";
import { coreKind } from "./kinds";

/** 版本的类型名：业务版本用 Schema 的 displayName，Core 类型用类型名。 */
export function useVersionType(version: Version | undefined) {
  const schema = useVersion(version?.category === "business" ? version.schemaVersionId : null);
  if (!version) return { name: null, resourceName: null };
  if (version.category !== "business") {
    const name = isEntitySchema(version)
      ? `${CATEGORY_NAMES.entity_schema} · ${version.content.displayName}`
      : CATEGORY_NAMES[version.category];
    return { name, resourceName: null };
  }
  if (schema.data && isEntitySchema(schema.data)) {
    return { name: schema.data.content.displayName, resourceName: schema.data.content.resourceName };
  }
  return { name: schema.isError ? CATEGORY_NAMES.business : null, resourceName: null };
}

/** 版本的类型：名称和颜色。 */
function useVersionKind(version: Version | undefined) {
  const type = useVersionType(version);
  const core = version && version.category !== "business" ? coreKind(version.category) : undefined;
  return { name: type.name, tint: core ? core.tint : tintOf(type.resourceName) };
}

/** 指向一个版本的标签，点击进入它所属的实体。放在别的链接里时用 plain，只显示不链接。 */
export function VersionChip({
  versionId,
  strong,
  showNumber,
  plain,
}: {
  versionId: string;
  strong?: boolean;
  showNumber?: boolean;
  plain?: boolean;
}) {
  const version = useVersion(versionId);
  const kind = useVersionKind(version.data);
  const chain = useEntityVersions(showNumber ? version.data?.entityId : null);

  if (version.isError) return <Chip dashed>{UNREADABLE}</Chip>;
  if (!version.data) return <Chip className="text-fg-3">…</Chip>;

  const number = chain.data ? versionNumber(chain.data, versionId) : null;
  const chip = (
    <Chip strong={!!strong}>
      <Dot color={kind.tint} />
      {kind.name ?? "…"}
      {number !== null && chain.data && <span className="text-fg-3">{versionLabel(number, chain.data.length)}</span>}
      {isObservation(version.data) && <span className="text-fg-3">{factTime(version.data.content)}</span>}
    </Chip>
  );
  if (plain) return chip;
  return (
    <Link to="/versions/$versionId" params={{ versionId }} className="hover:[&>span]:border-line-3">
      {chip}
    </Link>
  );
}

/** Observation 的事实时间，例如“14:00–16:00”；只知道一个边界时写这一刻。 */
function factTime(content: { startAt: string | null; endAt: string | null }): string {
  const start = parseTime(content.startAt);
  const end = parseTime(content.endAt);
  if (start !== null && end !== null) return `${clock(start)}–${clock(end)}`;
  const point = start ?? end;
  return point === null ? "时间未知" : clock(point);
}
