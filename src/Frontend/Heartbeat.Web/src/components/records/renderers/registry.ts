import { DesktopApplicationForegroundV1 } from "@/components/records/renderers/DesktopApplicationForegroundV1";
import type { RecordRenderer } from "@/components/records/renderers/types";

const renderers = new Map<string, RecordRenderer>([
  [rendererKey("desktop.application.foreground", 1), DesktopApplicationForegroundV1],
]);

function rendererKey(type: string, version: number): string {
  return `${type}@${version}`;
}

export function findRecordRenderer(type: string, version: number): RecordRenderer | null {
  return renderers.get(rendererKey(type, version)) ?? null;
}
