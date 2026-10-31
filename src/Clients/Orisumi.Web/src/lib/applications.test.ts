import { describe, expect, it } from "vitest";
import type { Version } from "@/api/types";
import { id } from "@/test/fixtures";
import { buildCatalog, installDetails, latestMembership, unassigned, type Identifier } from "./applications";

const qqMac: Identifier = { scheme: "macos.bundle-id", value: "com.tencent.qq" };
const qqAndroid: Identifier = { scheme: "android.package-name", value: "com.tencent.mobileqq" };
const lark: Identifier = { scheme: "macos.bundle-id", value: "com.electron.lark" };

function business(content: unknown, recordedAt = "2026-10-29T01:00:00.000Z"): Version {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "business",
    schemaVersionId: "schema-v1",
    recordedAt,
    content,
  };
}

const product = (name: string) => business({ name });
const membership = (identifier: Identifier, target: Version, recordedAt?: string) =>
  business({ identifier, productVersionId: target.versionId }, recordedAt);
const install = (clue: Identifier | null, path: string) =>
  business({ identifiers: [...(clue ? [clue] : []), { scheme: "macos.executable-path", value: path }] });

function catalogOf(products: Version[], memberships: Version[], installs: Version[]) {
  const targets = new Map(products.map((p) => [p.versionId, p.entityId]));
  return buildCatalog(products, memberships, installs, targets);
}

describe("归属", () => {
  it("跨设备线索相同的安装属于同一个产品，路径不参与", () => {
    const qq = product("QQ");
    const a = install(qqMac, "/Applications/QQ.app/Contents/MacOS/QQ");
    const b = install(qqMac, "/Users/me/Applications/QQ.app/Contents/MacOS/QQ");
    const catalog = catalogOf([qq], [membership(qqMac, qq)], [a, b]);
    expect(catalog.productOf(a)?.name).toBe("QQ");
    expect(catalog.productOf(b)?.name).toBe("QQ");
    expect(catalog.installsOf(qqMac)).toHaveLength(2);
  });

  it("一个产品的线索来自各自的线索归属，最近一次判断取保存得最晚的", () => {
    const qq = product("QQ");
    const earlier = membership(qqMac, qq, "2026-10-29T01:00:00.000Z");
    const later = membership(qqAndroid, qq, "2026-10-29T02:00:00.000Z");
    const catalog = catalogOf([qq], [earlier, later], []);
    expect(catalog.products[0]!.memberships.map((m) => m.identifier)).toEqual([qqMac, qqAndroid]);
    expect(latestMembership(catalog.products[0]!).version).toBe(later);
  });

  it("没有线索归属的产品不列出", () => {
    const catalog = catalogOf([product("QQ"), product("Lark")], [], []);
    expect(catalog.products).toEqual([]);
  });

  it("指向的产品已删除时，线索未归属", () => {
    const deleted = product("已删除");
    const app = install(qqMac, "/a");
    const catalog = buildCatalog([], [membership(qqMac, deleted)], [app], new Map());
    expect(catalog.productOf(app)).toBeNull();
    expect(unassigned(catalog).clues).toEqual([qqMac]);
  });

  it("线索归属引用产品的旧版本时，按实体归到产品", () => {
    const qq = product("QQ");
    const old = { ...qq, versionId: id() };
    const catalog = buildCatalog([qq], [membership(qqMac, old)], [], new Map([[old.versionId, qq.entityId]]));
    expect(catalog.ownerOf(qqMac)?.name).toBe("QQ");
  });

  it("没有匹配的线索未归属；只有本机线索的安装单独列出", () => {
    const qq = product("QQ");
    const pathOnly = install(null, "/usr/local/bin/tool");
    const catalog = catalogOf([qq], [membership(qqMac, qq)], [install(qqMac, "/a"), install(lark, "/b"), pathOnly]);
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
