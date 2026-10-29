// HTTP 契约的类型，按 src/Docs/Orisumi.Docs/content/docs/api/openapi.json 手写。

export type Category = "observation" | "entity_schema" | "input_set" | "business";

export interface Observation {
  observerVersionId: string;
  contentVersionId: string;
  inputSetVersionId: string | null;
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
}

export interface InputSet {
  members: string[];
}

export interface FieldDefinition {
  description: string;
  type: "string" | "boolean" | "integer" | "number" | "reference" | "object" | "array";
  required?: boolean;
  nullable?: boolean;
  fields?: Record<string, FieldDefinition>;
  items?: FieldDefinition;
}

export interface EntitySchema {
  displayName: string;
  description: string;
  resourceName: string;
  fields: Record<string, FieldDefinition>;
}

export type BusinessContent = Record<string, unknown>;

export interface Version<C = unknown> {
  entityId: string;
  versionId: string;
  previousVersionId: string | null;
  category: Category;
  schemaVersionId: string | null;
  recordedAt: string;
  content: C;
}

/** 提交时的版本：没有 recordedAt。 */
export type VersionSubmission<C = unknown> = Omit<Version<C>, "recordedAt">;

export interface SubmissionResult {
  versions: { versionId: string; recordedAt: string }[];
}

export interface VersionPage {
  versions: Version[];
  next: string | null;
}

export interface VersionList {
  versions: Version[];
}

export interface ReferenceLocation {
  path: string;
  targetVersionId: string;
}

export interface Referrer {
  version: Version;
  references: ReferenceLocation[];
}

export interface ReferrerPage {
  referrers: Referrer[];
  next: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  rule?: string;
}

export interface ListEntitiesQuery {
  category?: Category;
  resourceName?: string;
  limit?: number;
  after?: string;
}

export interface ListObservationsQuery {
  from: string;
  to: string;
  resourceName?: string;
  limit?: number;
  after?: string;
}

export interface ListReferrersQuery {
  versions?: "current" | "all";
  limit?: number;
  after?: string;
}

export function isObservation(version: Version): version is Version<Observation> {
  return version.category === "observation";
}

export function isInputSet(version: Version): version is Version<InputSet> {
  return version.category === "input_set";
}

export function isEntitySchema(version: Version): version is Version<EntitySchema> {
  return version.category === "entity_schema";
}
