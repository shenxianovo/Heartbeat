import type { ComponentType } from "react";

export interface RecordRendererProps {
  value: unknown;
}

export type RecordRenderer = ComponentType<RecordRendererProps>;

export interface RecordSummary {
  label: string;
  title?: string;
  tone?: "default" | "muted" | "attention";
}

export interface RecordPresentation {
  Renderer: RecordRenderer;
  summarize: (value: unknown) => RecordSummary;
}
