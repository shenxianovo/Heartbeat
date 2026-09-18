import type { RecordRendererProps, RecordSummary } from "@/components/records/renderers/types";

function object(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("桌面记录的值结构不匹配");
  }
  return value as Record<string, unknown>;
}

function required(value: unknown, name: string): string {
  if (typeof value !== "string" || !value.trim()) throw new Error(`桌面记录缺少 ${name}`);
  return value;
}

const awayLabels: Record<string, string> = {
  screen_locked: "锁屏",
  session_inactive: "会话未激活",
  display_sleep: "显示器休眠",
  system_sleep: "系统休眠",
};
const capabilityLabels: Record<string, string> = {
  application: "应用观测",
  window_title: "窗口标题",
  input: "输入观测",
};
const stateLabels: Record<string, string> = {
  available: "可用",
  permission_required: "缺少权限",
  unavailable: "无法观测",
};

export function summarizeDesktopAway(value: unknown): RecordSummary {
  const reason = required(object(value).reason, "reason");
  return { label: `离开 · ${awayLabels[reason] ?? reason}`, tone: "muted" };
}

export function summarizeDesktopStatus(value: unknown): RecordSummary {
  const parsed = object(value);
  const capability = required(parsed.capability, "capability");
  const state = required(parsed.state, "state");
  const label = `${capabilityLabels[capability] ?? capability} · ${stateLabels[state] ?? state}`;
  return {
    label,
    hover: typeof parsed.reason === "string" ? `${label} · ${parsed.reason}` : label,
    tone: state === "available" ? "muted" : "attention",
  };
}

export function summarizeDesktopInput(value: unknown): RecordSummary {
  const kind = required(object(value).kind, "kind");
  const labels: Record<string, string> = {
    key_down: "按键",
    mouse_button_down: "鼠标按钮",
    scroll: "滚动",
  };
  return { label: labels[kind] ?? kind };
}

function DesktopFact({ title, detail }: { title: string; detail: string }) {
  return (
    <div className="desktop-fact">
      <strong>{title}</strong>
      <span>{detail}</span>
    </div>
  );
}

export function DesktopAwayV1({ value }: RecordRendererProps) {
  const parsed = object(value);
  return (
    <DesktopFact
      title={summarizeDesktopAway(value).label}
      detail={required(parsed.device_id, "device_id")}
    />
  );
}

export function DesktopObservationStatusV1({ value }: RecordRendererProps) {
  const parsed = object(value);
  const reason = typeof parsed.reason === "string" && parsed.reason.trim() ? parsed.reason : null;
  return (
    <DesktopFact
      title={summarizeDesktopStatus(value).label}
      detail={reason ?? "能力可用不代表记录完整"}
    />
  );
}

export function DesktopInputEventV1({ value }: RecordRendererProps) {
  const parsed = object(value);
  const kind = required(parsed.kind, "kind");
  let detail: string;
  if (kind === "key_down") {
    detail = `${required(parsed.code_set, "code_set")} · code ${String(parsed.code)}`;
  } else if (kind === "mouse_button_down") {
    detail = `button ${String(parsed.button)}`;
  } else if (kind === "scroll") {
    detail = `x ${String(parsed.delta_x)} · y ${String(parsed.delta_y)} · ${String(parsed.unit)}`;
  } else {
    throw new Error("未知桌面输入类型");
  }
  return <DesktopFact title={kind.replaceAll("_", " ")} detail={detail} />;
}
