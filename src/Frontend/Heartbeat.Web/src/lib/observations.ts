import type { EntityResponse } from "@/api/types";

export interface ObservationItem {
  id: string;
  startAt: string | null;
  endAt: string | null;
  observerId: string | null;
  contentId: string | null;
}

export function toObservationItem(id: string, entity: EntityResponse): ObservationItem {
  return {
    id,
    startAt: typeof entity.properties.startAt === "string" ? entity.properties.startAt : null,
    endAt: typeof entity.properties.endAt === "string" ? entity.properties.endAt : null,
    observerId: typeof entity.references.observerId === "string" ? entity.references.observerId : null,
    contentId: typeof entity.references.contentId === "string" ? entity.references.contentId : null,
  };
}
