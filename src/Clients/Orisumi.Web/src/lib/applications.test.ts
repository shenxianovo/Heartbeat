import { describe, expect, it } from "vitest";
import type { InputSet, Observation, Version, VersionSubmission } from "@/api/types";
import { id } from "@/test/fixtures";
import { buildCatalog, installDetails, planEdit, unassigned, type Identifier } from "./applications";

const qqMac: Identifier = { scheme: "macos.bundle-id", value: "com.tencent.qq" };
const qqAndroid: Identifier = { scheme: "android.package-name", value: "com.tencent.mobileqq" };
const lark: Identifier = { scheme: "macos.bundle-id", value: "com.electron.lark" };

function product(name: string, identifiers: Identifier[], recordedAt = "2026-10-29T01:00:00.000Z"): Version {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "business",
    schemaVersionId: "product-schema-v1",
    recordedAt,
    content: { name, identifiers },
  };
}

function install(clue: Identifier | null, path: string): Version {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "business",
    schemaVersionId: "local-application-schema-v1",
    recordedAt: "2026-10-29T00:00:00.000Z",
    content: { identifiers: [...(clue ? [clue] : []), { scheme: "macos.executable-path", value: path }] },
  };
}

const context = { observerVersionId: "user-v1", productSchemaVersionId: "product-schema-v1", newId: id };

describe("归属", () => {
  it("跨设备线索相同的安装属于同一个产品，路径不参与", () => {
    const qq = product("QQ", [qqMac, qqAndroid]);
    const a = install(qqMac, "/Applications/QQ.app/Contents/MacOS/QQ");
    const b = install(qqMac, "/Users/me/Applications/QQ.app/Contents/MacOS/QQ");
    const catalog = buildCatalog([qq], [a, b]);
    expect(catalog.productOf(a)?.name).toBe("QQ");
    expect(catalog.productOf(b)?.name).toBe("QQ");
    expect(catalog.installsOf(qqMac)).toHaveLength(2);
  });

  it("一条线索写在两个产品中时，归到当前版本保存得最晚的那个", () => {
    const old = product("QQ", [qqMac], "2026-10-29T01:00:00.000Z");
    const recent = product("QQ 国际版", [qqMac], "2026-10-29T02:00:00.000Z");
    const catalog = buildCatalog([old, recent], [install(qqMac, "/a")]);
    expect(catalog.ownerOf(qqMac)?.name).toBe("QQ 国际版");
  });

  it("没有匹配的线索未归属；只有本机线索的安装单独列出", () => {
    const pathOnly = install(null, "/usr/local/bin/tool");
    const catalog = buildCatalog([product("QQ", [qqMac])], [install(qqMac, "/a"), install(lark, "/b"), pathOnly]);
    const loose = unassigned(catalog);
    expect(loose.clues).toEqual([lark]);
    expect(loose.withoutClues.map((x) => x.version)).toEqual([pathOnly]);
    expect(catalog.productOf(pathOnly)).toBeNull();
  });

  it("同一条线索的几个安装写出路径里不同的那一段", () => {
    const details = installDetails([install(qqMac, "/A/Release/QQ"), install(qqMac, "/A/Debug/QQ")]);
    expect(details).toEqual(["…/Release/…", "…/Debug/…"]);
    expect(installDetails([install(qqMac, "/A/QQ")])).toEqual([null]);
  });
});

describe("修改", () => {
  const qq = product("QQ", [qqMac, qqAndroid]);
  const larkProduct = product("Lark", [lark]);
  const androidInstall = install(qqAndroid, "/android");
  const catalog = buildCatalog([qq, larkProduct], [install(qqMac, "/a"), androidInstall]);

  const parts = (versions: VersionSubmission[]) => ({
    inputSet: versions[0] as VersionSubmission<InputSet>,
    products: versions.filter((v) => v.category === "business"),
    observations: versions.filter((v) => v.category === "observation") as VersionSubmission<Observation>[],
  });

  it("把线索移到新产品：InputSet、新产品首版、原产品新版本、每版一份用户本人的 Observation", () => {
    const plan = planEdit({ kind: "move", identifier: qqAndroid, to: { name: " QQ 轻聊版 " } }, catalog, context);
    expect(plan.versions.map((v) => v.category)).toEqual([
      "input_set",
      "business",
      "business",
      "observation",
      "observation",
    ]);
    const { inputSet, products, observations } = parts(plan.versions);
    const [created, source] = products;
    expect(created!.previousVersionId).toBeNull();
    expect(created!.content).toEqual({ name: "QQ 轻聊版", identifiers: [qqAndroid] });
    expect(source!.entityId).toBe(qq.entityId);
    expect(source!.previousVersionId).toBe(qq.versionId);
    expect(source!.content).toEqual({ name: "QQ", identifiers: [qqMac] });
    expect(inputSet.content.members).toEqual([qq.versionId, androidInstall.versionId]);
    for (const [i, observation] of observations.entries()) {
      expect(observation.content).toEqual({
        observerVersionId: "user-v1",
        contentVersionId: products[i]!.versionId,
        inputSetVersionId: inputSet.versionId,
        startAt: null,
        endAt: null,
        timeZone: null,
      });
    }
    expect(plan.deletes).toEqual([]);
  });

  it("把线索移到另一个产品时，先写加入线索的产品", () => {
    const plan = planEdit({ kind: "move", identifier: qqAndroid, to: catalog.products[1]! }, catalog, context);
    const { products } = parts(plan.versions);
    expect(products.map((p) => p.entityId)).toEqual([larkProduct.entityId, qq.entityId]);
    expect(products[0]!.content).toEqual({ name: "Lark", identifiers: [lark, qqAndroid] });
  });

  it("移出线索只改原产品", () => {
    const plan = planEdit({ kind: "release", identifier: qqAndroid }, catalog, context);
    const { products } = parts(plan.versions);
    expect(products).toHaveLength(1);
    expect(products[0]!.content).toEqual({ name: "QQ", identifiers: [qqMac] });
  });

  it("改名保留线索", () => {
    const plan = planEdit({ kind: "rename", product: catalog.products[0]!, name: "腾讯 QQ" }, catalog, context);
    expect(parts(plan.versions).products[0]!.content).toEqual({ name: "腾讯 QQ", identifiers: [qqMac, qqAndroid] });
  });

  it("合并：线索加入保留的产品，再删除被并入的", () => {
    const plan = planEdit({ kind: "merge", from: catalog.products[1]!, into: catalog.products[0]! }, catalog, context);
    expect(parts(plan.versions).products[0]!.content).toEqual({ name: "QQ", identifiers: [qqMac, qqAndroid, lark] });
    expect(plan.deletes).toEqual([larkProduct.entityId]);
  });

  it("删除产品不提交版本", () => {
    const plan = planEdit({ kind: "delete", product: catalog.products[1]! }, catalog, context);
    expect(plan.versions).toEqual([]);
    expect(plan.deletes).toEqual([larkProduct.entityId]);
  });
});
