import type { RecordObject, ListedObject, ObjectScope } from "@/api/types";
import { rangeToIso, type DateRange } from "@/lib/dates";

export function objectName(object: Pick<ListedObject, "id" | "name" | "key">) {
  return object.name || object.key || object.id;
}
export function objectKind(space: string | null) {
  if (space === null) return "对象";
  if (space === "device") return "设备";
  if (space.startsWith("app.")) return "应用";
  if (space === "vrchat.account") return "VRChat 账号";
  if (space === "vrchat.world") return "VRChat 世界";
  return space;
}
export function objectHref(id: string, range?: DateRange, contextObjectIds?: string[]) {
  const iso = range ? rangeToIso(range) : null;
  const params = new URLSearchParams(iso ? { from: iso.from, to: iso.to } : {});
  const path = [...new Set(contextObjectIds ?? [])];
  const ancestor = path.indexOf(id);
  for (const context of ancestor < 0 ? path : path.slice(0, ancestor))
    params.append("context", context);
  return `/objects/${encodeURIComponent(id)}${params.size ? `?${params}` : ""}`;
}
export function relatedObjectHref(id: string, range: DateRange | undefined, scope: ObjectScope) {
  return objectHref(id, range, [
    ...(scope.contextObjectIds ?? []),
    ...(scope.objectId ? [scope.objectId] : []),
  ]);
}

export function referencedObject(objects: RecordObject[], role: string) {
  const object = objects.find((item) => item.role === role);
  if (!object?.key) throw new Error(`记录缺少 ${role} 对象的原生标识`);
  return { ...object, key: object.key };
}
