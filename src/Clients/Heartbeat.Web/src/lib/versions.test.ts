import { describe, expect, it } from "vitest";
import type { Version } from "@/api/types";
import { changedFields, versionLabel, versionNumber } from "./versions";

function version(versionId: string, content: Record<string, unknown>): Version {
  return {
    entityId: "e",
    versionId,
    previousVersionId: null,
    category: "business",
    schemaVersionId: "s",
    recordedAt: "2026-10-25T00:00:00Z",
    content,
  };
}

describe("versionNumber / versionLabel", () => {
  const chain = [version("a", {}), version("b", {}), version("c", {})];

  it("序号从 1 开始，不在版本链中时为 null", () => {
    expect(versionNumber(chain, "b")).toBe(2);
    expect(versionNumber(chain, "x")).toBeNull();
  });

  it("已有更新的版本时写出当前版本号", () => {
    expect(versionLabel(9, 12)).toBe("v9 / v12");
    expect(versionLabel(12, 12)).toBe("v12");
  });
});

describe("changedFields", () => {
  it("列出相对前一版变化、新增和移除的字段", () => {
    const before = version("a", { endAt: "1", title: "x", gone: 1 });
    const after = version("b", { endAt: "2", title: "x", added: true });
    expect(changedFields(before, after)).toEqual(["endAt", "added", "gone"]);
  });

  it("首版没有前一版", () => {
    expect(changedFields(undefined, version("a", { x: 1 }))).toEqual([]);
  });
});
