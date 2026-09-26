import type { RecordRendererProps } from "@/components/records/renderers/types";

export function stringifyJson(value: unknown): string {
  if (value === undefined) return "undefined";
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return "[无法序列化的值]";
  }
}

export function FallbackJsonRenderer({ value, error }: RecordRendererProps & { error?: string }) {
  return (
    <div className="fallback-renderer" role={error ? "alert" : undefined}>
      <span>{error ?? "未提供专用展示"}</span>
      <pre>{stringifyJson(value)}</pre>
    </div>
  );
}
