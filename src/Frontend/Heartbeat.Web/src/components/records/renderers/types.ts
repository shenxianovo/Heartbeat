import type { ComponentType } from "react";

export interface RecordRendererProps {
  value: unknown;
}

export type RecordRenderer = ComponentType<RecordRendererProps>;

export interface RecordSummary {
  label: string;
  group?: { id: string; label: string };
  hover?: string;
  tone?: "default" | "muted" | "attention";
}

/** Track time mode owns geometry; a protocol presentation supplies content only. */
export interface RecordPresentation {
  label: string;
  Renderer?: RecordRenderer;
  summarize: (value: unknown) => RecordSummary;
}
