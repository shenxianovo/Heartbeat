/**
 * Heartbeat HTTP 契约（以 content/docs/api/openapi.json 为权威来源）。
 * 所有实体读取返回统一信封；保存请求外层只有 references 与 properties。
 */

export const ENTITY_CATEGORIES = ["entity", "observation", "observer", "entity_schema"] as const;
export type EntityCategory = (typeof ENTITY_CATEGORIES)[number];

export const CATEGORY_LABEL: Record<EntityCategory, string> = {
  entity: "业务实体",
  observation: "观测",
  observer: "观测主体",
  entity_schema: "实体定义",
};

export interface EntityResponse {
  category: EntityCategory;
  /** 仅 category=entity 返回，指向其 EntitySchema。 */
  schemaId?: string;
  references: Record<string, string | null>;
  properties: Record<string, unknown>;
}

export interface EntityEnvelope {
  references: Record<string, string | null>;
  properties: Record<string, unknown>;
}

export interface ProblemDetails {
  type: string;
  title: string;
  status: number;
  detail: string;
}

/** Observation 固定属性（properties）。未知必须为 null，不允许省略。 */
export interface ObservationProperties {
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
}

export interface ObservationReferences {
  observerId: string;
  contentId: string;
  schemaId: string;
}

/** EntitySchema 字段定义（OpenAPI components/schemas/EntityFieldDefinition 子集渲染所需）。 */
export interface FieldDefinition {
  type: string;
  required?: boolean;
  nullable?: boolean;
  description?: string;
  fields?: Record<string, FieldDefinition>;
  items?: FieldDefinition;
  values?: Record<string, FieldDefinition>;
  reference?: boolean;
}
