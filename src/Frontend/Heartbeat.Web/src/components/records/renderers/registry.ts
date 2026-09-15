import {
  DesktopApplicationForegroundV1,
  summarizeDesktopApplication,
} from "@/components/records/renderers/DesktopApplicationForegroundV1";
import {
  DesktopAwayV1,
  DesktopInputEventV1,
  DesktopObservationStatusV1,
  summarizeDesktopAway,
  summarizeDesktopInput,
  summarizeDesktopStatus,
} from "@/components/records/renderers/DesktopSystemV1";
import type {
  RecordPresentation,
  RecordRenderer,
  RecordSummary,
} from "@/components/records/renderers/types";

const renderers = new Map<string, RecordPresentation>([
  [
    rendererKey("desktop.application.foreground", 1),
    { Renderer: DesktopApplicationForegroundV1, summarize: summarizeDesktopApplication },
  ],
  [
    rendererKey("desktop.system.away", 1),
    { Renderer: DesktopAwayV1, summarize: summarizeDesktopAway },
  ],
  [
    rendererKey("desktop.input.event", 1),
    { Renderer: DesktopInputEventV1, summarize: summarizeDesktopInput },
  ],
  [
    rendererKey("desktop.observation.status", 1),
    { Renderer: DesktopObservationStatusV1, summarize: summarizeDesktopStatus },
  ],
]);

function rendererKey(type: string, version: number): string {
  return `${type}@${version}`;
}

export function findRecordRenderer(type: string, version: number): RecordRenderer | null {
  return renderers.get(rendererKey(type, version))?.Renderer ?? null;
}

export function summarizeRecord(
  type: string,
  version: number,
  value: unknown,
  fallback: string,
): RecordSummary {
  try {
    return renderers.get(rendererKey(type, version))?.summarize(value) ?? { label: fallback };
  } catch {
    return { label: fallback };
  }
}
