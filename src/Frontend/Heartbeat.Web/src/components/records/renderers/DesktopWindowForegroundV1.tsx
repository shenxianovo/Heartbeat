import type { RecordRendererProps, RecordSummary } from "@/components/records/renderers/types";
import { isObject, nonEmptyString } from "@/components/records/renderers/values";

interface ForegroundWindowValue {
  title: string;
}

function parseForegroundWindow(value: unknown): ForegroundWindowValue {
  if (!isObject(value) || !isObject(value.window)) {
    throw new Error("前台窗口记录的值结构不匹配");
  }
  if (!nonEmptyString(value.window.title)) {
    throw new Error("前台窗口记录缺少窗口标题");
  }
  return { title: value.window.title };
}

export function summarizeDesktopWindow(value: unknown): RecordSummary {
  return { label: parseForegroundWindow(value).title };
}

export function DesktopWindowForegroundV1({ value }: RecordRendererProps) {
  const parsed = parseForegroundWindow(value);

  return (
    <div className="application-record">
      <div className="application-identity">
        <strong>{parsed.title}</strong>
      </div>
    </div>
  );
}
