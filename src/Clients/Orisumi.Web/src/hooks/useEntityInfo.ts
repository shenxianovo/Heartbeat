import { isEntitySchema, isInputSet, isObservation, type EntitySchema, type Version } from "@/api/types";
import { roleOf, ROLE_NAMES, thingColor, type Role } from "@/lib/roles";
import { clock, parseTime } from "@/lib/time";
import { businessTitle } from "@/lib/titles";
import { CATEGORY_NAMES } from "@/lib/versions";
import { useVersion } from "./queries";
import { useTypes } from "./useTypes";

/** 一个版本在界面上的样子：角色、名字、类型名，以及事物的颜色。 */
export interface EntityInfo {
  version: Version;
  schema: Version<EntitySchema> | null;
  role: Role;
  roleName: string;
  title: string;
  typeName: string;
  /** 只有事物有颜色。 */
  color: string | null;
}

export function describeVersion(
  version: Version,
  schema: Version<EntitySchema> | null,
  role: Role,
): Omit<EntityInfo, "version" | "schema" | "role"> {
  const typeName =
    version.category === "business" ? (schema?.content.displayName ?? "业务实体") : CATEGORY_NAMES[version.category];
  let title = typeName;
  if (isObservation(version)) title = timeLabel(version.content.startAt, version.content.endAt);
  else if (isInputSet(version)) title = `${version.content.members.length} 个输入`;
  else if (isEntitySchema(version)) title = version.content.displayName;
  else title = businessTitle(version.content, schema?.content.fields, schema?.content.resourceName)?.text ?? typeName;
  return { roleName: ROLE_NAMES[role], title, typeName, color: role === "thing" ? thingColor(title) : null };
}

export function timeLabel(startAt: string | null, endAt: string | null): string {
  const start = parseTime(startAt);
  const end = parseTime(endAt);
  if (start !== null && end !== null) return start === end ? clock(start) : `${clock(start)}–${clock(end)}`;
  if (start !== null || end !== null) return clock((start ?? end)!);
  return "时间未知";
}

/** 按 VersionId 读取并描述一个版本。读不到时 info 为 null，unreadable 为 true。 */
export function useEntityInfo(versionId: string | null | undefined) {
  const version = useVersion(versionId);
  return useDescribe(version.data, version.isPending && !!versionId, version.isError);
}

export function useDescribe(version: Version | undefined, pending = false, failed = false) {
  const schema = useVersion(version?.category === "business" ? version.schemaVersionId : null);
  const types = useTypes();
  if (!version) return { info: null, pending, unreadable: failed };
  const schemaVersion = schema.data && isEntitySchema(schema.data) ? schema.data : null;
  const role = types.data
    ? roleOf(version, schemaVersion?.content.resourceName, types.data)
    : roleOf(version, null, EMPTY);
  return {
    info: { version, schema: schemaVersion, role, ...describeVersion(version, schemaVersion, role) } as EntityInfo,
    pending: false,
    unreadable: false,
  };
}

const EMPTY = { contentTypes: new Set<string>(), observerTypes: new Set<string>() };
