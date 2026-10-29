import { describe, expect, it } from "vitest";
import type { Category, Version } from "@/api/types";
import { differences, distinctSegment, roleOf } from "./roles";

function version(category: Category): Version {
  return {
    entityId: "e",
    versionId: "v",
    previousVersionId: null,
    category,
    schemaVersionId: null,
    recordedAt: "2026-10-29T00:00:00Z",
    content: {},
  };
}

const index = { contentTypes: new Set(["periods"]), observerTypes: new Set(["observers"]) };

describe("roleOf", () => {
  it("Core 类型按 category，业务实体按它被谁引用", () => {
    expect(roleOf(version("observation"), null, index)).toBe("experience");
    expect(roleOf(version("input_set"), null, index)).toBe("evidence");
    expect(roleOf(version("entity_schema"), null, index)).toBe("definition");
    expect(roleOf(version("business"), "observers", index)).toBe("source");
    expect(roleOf(version("business"), "periods", index)).toBe("experience");
    expect(roleOf(version("business"), "local-applications", index)).toBe("thing");
  });
});

describe("distinctSegment", () => {
  it("只留下各条路径不同的那一段", () => {
    const release = "/Users/a/Build/Products/Release/Orisumi.app/Contents/MacOS/Orisumi";
    const debug = "/Users/a/Build/Products/Debug/Orisumi.app/Contents/MacOS/Orisumi";
    expect(distinctSegment(release, [release, debug])).toBe("…/Release/…");
    expect(distinctSegment(debug, [release, debug])).toBe("…/Debug/…");
    expect(distinctSegment(release, [release])).toBeNull();
  });
});

describe("differences", () => {
  it("同名的安装写出可执行文件路径里不同的那一段", () => {
    const app = (path: string) => ({
      identifiers: [
        { scheme: "macos.bundle-id", value: "com.shenxianovo.orisumi" },
        { scheme: "macos.executable-path", value: path },
      ],
    });
    expect(
      differences([
        app("/a/Build/Products/Release/Orisumi.app/Contents/MacOS/Orisumi"),
        app("/a/Build/Products/Debug/Orisumi.app/Contents/MacOS/Orisumi"),
      ]),
    ).toEqual(["…/Release/…", "…/Debug/…"]);
  });

  it("内容完全相同时没有补充", () => {
    expect(differences([{ name: "x" }, { name: "x" }])).toEqual([null, null]);
  });
});
