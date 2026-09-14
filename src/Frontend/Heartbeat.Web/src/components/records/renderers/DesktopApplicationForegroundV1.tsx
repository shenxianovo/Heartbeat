import type { RecordRendererProps } from "@/components/records/renderers/types";

interface ForegroundApplicationValue {
  deviceId: string;
  platform: string;
  idKind: string;
  applicationId: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function parseForegroundApplication(value: unknown): ForegroundApplicationValue {
  if (!isObject(value) || !nonEmptyString(value.device_id) || !isObject(value.application)) {
    throw new Error("前台应用记录的值结构不匹配");
  }

  const application = value.application;
  if (
    !nonEmptyString(application.platform) ||
    !nonEmptyString(application.id_kind) ||
    !nonEmptyString(application.id)
  ) {
    throw new Error("前台应用记录缺少应用标识");
  }

  return {
    deviceId: value.device_id,
    platform: application.platform,
    idKind: application.id_kind,
    applicationId: application.id,
  };
}

function labelIdKind(idKind: string): string {
  if (idKind === "bundle_id") return "Bundle ID";
  if (idKind === "package_name") return "Package";
  return idKind.replaceAll("_", " ");
}

export function DesktopApplicationForegroundV1({ value }: RecordRendererProps) {
  const parsed = parseForegroundApplication(value);

  return (
    <div className="application-record">
      <div className="application-glyph" aria-hidden="true">
        {parsed.applicationId.slice(0, 1).toLocaleUpperCase()}
      </div>
      <div className="application-identity">
        <strong>{parsed.applicationId}</strong>
        <span>
          {parsed.platform.toLocaleUpperCase()} · {labelIdKind(parsed.idKind)}
        </span>
      </div>
      <span className="device-chip" title={parsed.deviceId}>
        {parsed.deviceId}
      </span>
    </div>
  );
}
