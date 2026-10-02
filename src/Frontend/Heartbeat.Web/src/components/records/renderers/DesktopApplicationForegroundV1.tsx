import type { RecordObject } from "@/api/types";
import type { RecordRendererProps, RecordSummary } from "./types";
import { referencedObject } from "@/components/objects/model";

export function summarizeDesktopApplication(
  _value: unknown,
  objects: RecordObject[],
): RecordSummary {
  const application = referencedObject(objects, "application");
  const label = application.name || application.key;
  return { label, group: { id: application.id, label } };
}
export function DesktopApplicationForegroundV1({ objects }: RecordRendererProps) {
  const application = referencedObject(objects, "application");
  return (
    <div className="application-record">
      <div className="application-identity">
        <strong>{application.name || application.key}</strong>
      </div>
    </div>
  );
}
