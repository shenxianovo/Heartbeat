import type { ComponentType } from "react";

export interface RecordRendererProps {
  value: unknown;
}

export type RecordRenderer = ComponentType<RecordRendererProps>;
