import { isInputSet, isObservation, type FieldDefinition, type Version } from "@/api/types";

/** 一个版本引用的另一个版本，path 是引用在内容里的位置。 */
export interface OutgoingReference {
  path: string;
  versionId: string;
}

/**
 * 这个版本引用了哪些版本：Observation 的 Observer、观测内容和 InputSet，InputSet 的每个输入，
 * 业务版本里 Schema 定义为 reference 的字段，包括 reference 数组的每一项和对象里的嵌套字段。
 */
export function outgoingReferences(
  version: Version,
  fields: Record<string, FieldDefinition> | undefined,
): OutgoingReference[] {
  if (isObservation(version)) {
    const content = version.content;
    return [
      { path: "observerVersionId", versionId: content.observerVersionId },
      { path: "contentVersionId", versionId: content.contentVersionId },
      ...(content.inputSetVersionId ? [{ path: "inputSetVersionId", versionId: content.inputSetVersionId }] : []),
    ];
  }
  if (isInputSet(version)) {
    return version.content.members.map((versionId, i) => ({ path: `members[${i}]`, versionId }));
  }
  if (version.category !== "business" || !fields) return [];
  const found: OutgoingReference[] = [];
  collect(version.content, fields, "", found);
  return found;
}

function collect(value: unknown, fields: Record<string, FieldDefinition>, prefix: string, found: OutgoingReference[]) {
  if (!value || typeof value !== "object") return;
  const record = value as Record<string, unknown>;
  for (const [name, definition] of Object.entries(fields)) {
    visit(record[name], definition, prefix ? `${prefix}.${name}` : name, found);
  }
}

function visit(value: unknown, definition: FieldDefinition, path: string, found: OutgoingReference[]) {
  if (value === null || value === undefined) return;
  if (definition.type === "reference" && typeof value === "string") found.push({ path, versionId: value });
  else if (definition.type === "array" && definition.items && Array.isArray(value))
    value.forEach((item, i) => visit(item, definition.items!, `${path}[${i}]`, found));
  else if (definition.type === "object" && definition.fields) collect(value, definition.fields, path, found);
}
