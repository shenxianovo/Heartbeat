import { describe, expect, it } from "vitest";
import { ApiError, type HeartbeatSource } from "@/api/client";
import type { Version } from "@/api/types";
import { categoryInfo, categoryOf } from "./categories";
import { nameObservers, resolveObserver, unreadableIdentity, type ObserverIdentity } from "./observers";

function identity(entityId: string, typeName: string): ObserverIdentity {
  return { key: entityId, entityId, typeName, resourceName: null, category: "other" };
}

describe("nameObservers", () => {
  it("名称取类型名", () => {
    const names = nameObservers([identity("0192b056-7300-7000-8000-00000000a1b2", "前台状态")]);
    expect(names.get("0192b056-7300-7000-8000-00000000a1b2")).toBe("前台状态");
  });

  it("同名的 Observer 加 EntityId 的末 4 位", () => {
    const names = nameObservers([
      identity("0192b056-7300-7000-8000-00000000a1b2", "前台状态"),
      identity("0192b056-7300-7000-8000-00000000c3d4", "前台状态"),
    ]);
    expect(names.get("0192b056-7300-7000-8000-00000000a1b2")).toBe("前台状态 a1b2");
    expect(names.get("0192b056-7300-7000-8000-00000000c3d4")).toBe("前台状态 c3d4");
  });

  it("读不到的版本显示为不可读取", () => {
    const names = nameObservers([unreadableIdentity("v1")]);
    expect(names.get("unreadable:v1")).toBe("不可读取");
  });
});

describe("resolveObserver", () => {
  const schema: Version = {
    entityId: "schema",
    versionId: "schema-v1",
    previousVersionId: null,
    category: "entity_schema",
    schemaVersionId: null,
    recordedAt: "2026-10-25T00:00:00Z",
    content: { displayName: "前台状态", description: "", resourceName: "foreground-state-observers", fields: {} },
  };
  const observer: Version = {
    entityId: "observer",
    versionId: "observer-v1",
    previousVersionId: null,
    category: "business",
    schemaVersionId: "schema-v1",
    recordedAt: "2026-10-25T00:00:00Z",
    content: {},
  };

  function source(versions: Version[]): HeartbeatSource {
    return {
      getVersion: async (versionId) => {
        const found = versions.find((v) => v.versionId === versionId);
        if (!found) throw new ApiError(404, "not found");
        return found;
      },
    } as HeartbeatSource;
  }

  it("读取 Observer 版本和它的 Schema 版本", async () => {
    await expect(resolveObserver(source([schema, observer]), "observer-v1")).resolves.toEqual({
      key: "observer",
      entityId: "observer",
      typeName: "前台状态",
      resourceName: "foreground-state-observers",
      category: "desktop",
    });
  });

  it("Observer 版本读不到时成为单独的 Observer", async () => {
    await expect(resolveObserver(source([]), "gone")).resolves.toEqual(unreadableIdentity("gone"));
  });
});

describe("categoryOf", () => {
  it("按 resourceName 查映射表，表中没有的归入其他", () => {
    expect(categoryOf("foreground-state-observers")).toBe("desktop");
    expect(categoryOf("unknown-observers")).toBe("other");
    expect(categoryOf(null)).toBe("other");
    expect(categoryInfo("other").label).toBe("其他");
  });
});
