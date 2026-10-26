import type { Observation, Version } from "@/api/types";

let counter = 0;

/** 测试用的 UUIDv7 形状的 ID，按调用顺序递增。 */
export function id(): string {
  counter += 1;
  return `0192b056-7300-7000-8000-${counter.toString(16).padStart(12, "0")}`;
}

export function observation(
  overrides: Omit<Partial<Version<Observation>>, "content"> & { content?: Partial<Observation> } = {},
): Version<Observation> {
  const { content, ...rest } = overrides;
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "observation",
    schemaVersionId: null,
    recordedAt: "2026-10-25T06:00:00.000Z",
    ...rest,
    content: {
      observerVersionId: "observer-v1",
      contentVersionId: id(),
      inputSetVersionId: null,
      startAt: "2026-10-25T05:00:00.000Z",
      endAt: "2026-10-25T06:00:00.000Z",
      timeZone: "Asia/Shanghai",
      ...content,
    },
  };
}
