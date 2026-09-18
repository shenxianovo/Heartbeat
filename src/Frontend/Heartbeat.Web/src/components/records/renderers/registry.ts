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
import {
  DesktopWindowForegroundV1,
  summarizeDesktopWindow,
} from "@/components/records/renderers/DesktopWindowForegroundV1";
import type {
  RecordPresentation,
  RecordRenderer,
  RecordSummary,
} from "@/components/records/renderers/types";
import type { TimelineRecord, TrackSummary } from "@/api/types";

const renderers = new Map<string, RecordPresentation>([
  [
    rendererKey("desktop.application.foreground", 1),
    {
      label: "前台应用",
      Renderer: DesktopApplicationForegroundV1,
      summarize: summarizeDesktopApplication,
    },
  ],
  [
    rendererKey("desktop.window.foreground", 1),
    {
      label: "前台窗口",
      Renderer: DesktopWindowForegroundV1,
      summarize: summarizeDesktopWindow,
    },
  ],
  [
    rendererKey("desktop.system.away", 1),
    { label: "离开信号", Renderer: DesktopAwayV1, summarize: summarizeDesktopAway },
  ],
  [
    rendererKey("desktop.input.event", 1),
    { label: "输入密度", Renderer: DesktopInputEventV1, summarize: summarizeDesktopInput },
  ],
  [
    rendererKey("desktop.observation.status", 1),
    { label: "观测状态", Renderer: DesktopObservationStatusV1, summarize: summarizeDesktopStatus },
  ],
]);
const summaries = new WeakMap<TimelineRecord, { key: string; summary: RecordSummary }>();

function rendererKey(type: string, version: number): string {
  return `${type}@${version}`;
}

export function findRecordRenderer(type: string, version: number): RecordRenderer | null {
  return renderers.get(rendererKey(type, version))?.Renderer ?? null;
}

export function describeRecord(track: TrackSummary, record: TimelineRecord): RecordSummary {
  const key = `${track.type}@${track.version}/${track.timeMode}`;
  const cached = summaries.get(record);
  if (cached?.key === key) return cached.summary;
  const fallback = track.timeMode === "point" ? "瞬时记录" : "时间区间";
  let summary: RecordSummary;
  try {
    summary = renderers.get(rendererKey(track.type, track.version))?.summarize(record.value) ?? {
      label: fallback,
    };
  } catch {
    summary = { label: fallback };
  }
  summaries.set(record, { key, summary });
  return summary;
}

export function recordTypeLabel(type: string, version: number): string {
  return renderers.get(rendererKey(type, version))?.label ?? `${type} · v${version}`;
}
