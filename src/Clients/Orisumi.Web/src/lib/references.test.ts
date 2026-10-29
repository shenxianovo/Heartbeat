import { describe, expect, it } from "vitest";
import type { FieldDefinition, Version } from "@/api/types";
import { observation } from "@/test/fixtures";
import { outgoingReferences, referenceLabel } from "./references";

function business(content: Record<string, unknown>): Version {
  return {
    entityId: "e",
    versionId: "v",
    previousVersionId: null,
    category: "business",
    schemaVersionId: "s",
    recordedAt: "2026-10-25T00:00:00Z",
    content,
  };
}

const ref: FieldDefinition = { description: "", type: "reference" };

describe("outgoingReferences", () => {
  it("Observation 引用 Observer、观测内容和 InputSet", () => {
    const version = observation({ content: { contentVersionId: "c", inputSetVersionId: "i" } });
    expect(outgoingReferences(version, undefined)).toEqual([
      { path: "observerVersionId", versionId: "observer-v1" },
      { path: "contentVersionId", versionId: "c" },
      { path: "inputSetVersionId", versionId: "i" },
    ]);
  });

  it("业务版本只取 Schema 定义为 reference 的字段，包括数组项和嵌套字段", () => {
    const fields: Record<string, FieldDefinition> = {
      title: { description: "", type: "string" },
      author: ref,
      tags: { description: "", type: "array", items: ref },
      meta: { description: "", type: "object", fields: { source: ref } },
    };
    const version = business({ title: "x", author: "a", tags: ["t1", "t2"], meta: { source: "s1" } });
    expect(outgoingReferences(version, fields)).toEqual([
      { path: "author", versionId: "a" },
      { path: "tags[0]", versionId: "t1" },
      { path: "tags[1]", versionId: "t2" },
      { path: "meta.source", versionId: "s1" },
    ]);
  });

  it("读不到 Schema 时业务版本没有可识别的引用", () => {
    expect(outgoingReferences(business({ author: "a" }), undefined)).toEqual([]);
  });
});

describe("referenceLabel", () => {
  it("去掉内容的根和 VersionId 后缀", () => {
    expect(referenceLabel("/content/localApplicationVersionId")).toBe("localApplication");
    expect(referenceLabel("observerVersionId")).toBe("observer");
  });

  it("数组下标写成方括号，嵌套字段用点连接", () => {
    expect(referenceLabel("/content/items/0/targetVersionId")).toBe("items[0].target");
    expect(referenceLabel("members[2]")).toBe("members[2]");
    expect(referenceLabel("tagsVersionId[]")).toBe("tags[]");
  });
});
