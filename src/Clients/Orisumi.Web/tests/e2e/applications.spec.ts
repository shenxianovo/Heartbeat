import { expect, test, type Page } from "@playwright/test";

// 应用页的修改：用路由拦截在内存里模拟后端，保存提交的版本、列出当前版本、删除实体，
// 检查每次修改提交的版本和页面的结果。

interface Version {
  entityId: string;
  versionId: string;
  previousVersionId: string | null;
  category: string;
  schemaVersionId: string | null;
  recordedAt: string;
  content: Record<string, unknown>;
}

let counter = 0;
const uuid = () => `0192b056-7300-7000-8000-${(++counter).toString(16).padStart(12, "0")}`;

const PRODUCT_SCHEMA = "019e1bbb-ec00-7000-8000-000000000004";
const qq = { scheme: "macos.bundle-id", value: "com.tencent.qq" };
const intl = { scheme: "macos.bundle-id", value: "com.tencent.qq.intl" };
const lark = { scheme: "macos.bundle-id", value: "com.electron.lark" };

class Backend {
  readonly entities = new Map<string, Version[]>();
  readonly submissions: Version[][] = [];

  add(entityId: string, category: string, schemaVersionId: string | null, content: Record<string, unknown>) {
    const version = {
      entityId,
      versionId: uuid(),
      previousVersionId: null,
      category,
      schemaVersionId,
      recordedAt: new Date().toISOString(),
      content,
    };
    this.entities.set(entityId, [version]);
    return version;
  }

  current(entityId: string) {
    return this.entities.get(entityId)?.at(-1);
  }

  resourceOf(version: Version) {
    for (const versions of this.entities.values())
      for (const v of versions)
        if (v.versionId === version.schemaVersionId) return (v.content as { resourceName: string }).resourceName;
    return null;
  }

  products() {
    return [...this.entities.values()]
      .map((versions) => versions.at(-1)!)
      .filter((v) => this.resourceOf(v) === "application-products");
  }

  async serve(page: Page) {
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname.replace(/^\/api/, "");
        const notFound = () => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
        if (request.method() === "POST" && path === "/versions") {
          const versions = (request.postDataJSON() as { versions: Omit<Version, "recordedAt">[] }).versions.map(
            (v) => ({ ...v, recordedAt: new Date().toISOString() }),
          );
          this.submissions.push(versions);
          for (const v of versions) this.entities.set(v.entityId, [...(this.entities.get(v.entityId) ?? []), v]);
          return route.fulfill({
            json: { versions: versions.map(({ versionId, recordedAt }) => ({ versionId, recordedAt })) },
          });
        }
        if (request.method() === "DELETE") {
          this.entities.delete(path.split("/")[2]!);
          return route.fulfill({ status: 204 });
        }
        if (path === "/observations") return route.fulfill({ json: { versions: [], next: null } });
        if (path === "/entities") {
          const resourceName = url.searchParams.get("resourceName");
          const category = url.searchParams.get("category");
          const all = [...this.entities.values()].map((versions) => versions.at(-1)!);
          if (resourceName) {
            if (!all.some((v) => v.category === "entity_schema" && v.content.resourceName === resourceName))
              return notFound();
            return route.fulfill({
              json: { versions: all.filter((v) => this.resourceOf(v) === resourceName), next: null },
            });
          }
          return route.fulfill({
            json: { versions: all.filter((v) => !category || v.category === category), next: null },
          });
        }
        const [, kind, id, rest] = path.split("/");
        if (kind === "versions") {
          for (const versions of this.entities.values()) {
            const found = versions.find((v) => v.versionId === id);
            if (found) return route.fulfill({ json: found });
          }
          return notFound();
        }
        if (kind === "entities" && id) {
          const versions = this.entities.get(id);
          if (!versions) return notFound();
          if (rest === "versions") return route.fulfill({ json: { versions } });
          if (rest === "referrers") {
            const targets = new Set(versions.map((v) => v.versionId));
            const referrers = [...this.entities.values()]
              .flat()
              .filter((v) => Object.values(v.content).some((value) => targets.has(value as string)))
              .map((version) => ({ version, references: [] }));
            return route.fulfill({ json: { referrers, next: null } });
          }
          return route.fulfill({ json: versions.at(-1) });
        }
        return notFound();
      },
    );
  }
}

function seed() {
  const backend = new Backend();
  const installs = backend.add(uuid(), "entity_schema", null, {
    displayName: "本机应用",
    description: "表示某台设备上的一个应用安装。",
    resourceName: "local-applications",
    fields: {},
  });
  const products = backend.add(PRODUCT_SCHEMA, "entity_schema", null, {
    displayName: "应用产品",
    description: "表示用户认知中的一款软件。",
    resourceName: "application-products",
    fields: {},
  });
  for (const [clue, path] of [
    [qq, "/Applications/QQ.app/Contents/MacOS/QQ"],
    [intl, "/Applications/QQ Intl.app/Contents/MacOS/QQ"],
    [lark, "/Applications/Lark.app/Contents/MacOS/Lark"],
  ] as const)
    backend.add(uuid(), "business", installs.versionId, {
      identifiers: [clue, { scheme: "macos.executable-path", value: path }],
    });
  backend.add(uuid(), "business", products.versionId, { name: "QQ", identifiers: [qq, intl] });
  backend.add(uuid(), "business", products.versionId, { name: "Lark", identifiers: [lark] });
  return backend;
}

test("把线索分到新产品：第一次修改时创建用户本人，产品版本由用户本人陈述", async ({ page }) => {
  const backend = seed();
  await backend.serve(page);
  await page.goto("/applications");
  await expect(page.getByText("2 个产品 · 3 个安装")).toBeVisible();

  await page.getByRole("button", { name: /com\.tencent\.qq\.intl/ }).click();
  await page.getByLabel("选择产品", { exact: true }).fill("QQ 国际版");
  await page.getByLabel("选择产品", { exact: true }).press("Enter");

  await expect(page.getByRole("status")).toHaveText("已把 com.tencent.qq.intl 归入新产品「QQ 国际版」");
  await expect(page.getByRole("article", { name: "应用产品 QQ 国际版" })).toBeVisible();
  const [batch] = backend.submissions;
  expect(batch!.map((v) => v.category)).toEqual([
    "entity_schema",
    "business",
    "input_set",
    "business",
    "business",
    "observation",
    "observation",
  ]);
  const user = batch![1]!;
  const observations = batch!.filter((v) => v.category === "observation");
  expect(observations.every((o) => o.content.observerVersionId === user.versionId && o.content.startAt === null)).toBe(
    true,
  );
  const names = backend.products().map((p) => [p.content.name, (p.content.identifiers as unknown[]).length]);
  expect(names).toEqual(
    expect.arrayContaining([
      ["QQ", 1],
      ["QQ 国际版", 1],
    ]),
  );
});

test("拖动线索到另一个产品，再删除空的产品", async ({ page }) => {
  const backend = seed();
  await backend.serve(page);
  await page.goto("/applications");
  const qqCard = page.getByRole("article", { name: "应用产品 QQ" });
  await qqCard.waitFor();

  await page.getByRole("button", { name: /com\.electron\.lark/ }).dragTo(qqCard);
  await expect(page.getByRole("status")).toHaveText("已把 com.electron.lark 归入「QQ」");
  await expect(qqCard.getByRole("button", { name: /com\.electron\.lark/ })).toBeVisible();

  await page.getByRole("button", { name: "Lark 的操作" }).click();
  await page.getByRole("button", { name: "删除…" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "删除", exact: true }).click();
  await expect(page.getByRole("status")).toHaveText("已删除「Lark」");
  await expect(page.getByRole("article", { name: "应用产品 Lark" })).toHaveCount(0);
  expect(backend.products()).toHaveLength(1);
});

test("没有本机应用时的空状态", async ({ page }) => {
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } }),
  );
  await page.goto("/applications");
  await expect(page.getByText("还没有本机应用")).toBeVisible();
});
