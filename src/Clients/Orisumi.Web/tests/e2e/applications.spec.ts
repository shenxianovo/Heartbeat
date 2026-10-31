import { expect, test, type Page } from "@playwright/test";

// 应用页：用路由拦截在内存里模拟后端的读取，检查产品、线索、未归属和判断记录。

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

const qq = { scheme: "macos.bundle-id", value: "com.tencent.qq" };
const intl = { scheme: "macos.bundle-id", value: "com.tencent.qq.intl" };
const lark = { scheme: "macos.bundle-id", value: "com.electron.lark" };

class Backend {
  readonly versions: Version[] = [];

  add(category: string, schemaVersionId: string | null, content: Record<string, unknown>) {
    const version = {
      entityId: uuid(),
      versionId: uuid(),
      previousVersionId: null,
      category,
      schemaVersionId,
      recordedAt: new Date().toISOString(),
      content,
    };
    this.versions.push(version);
    return version;
  }

  schema(resourceName: string, displayName: string) {
    return this.add("entity_schema", null, { displayName, description: `${displayName}。`, resourceName, fields: {} });
  }

  resourceOf(version: Version) {
    return this.versions.find((v) => v.versionId === version.schemaVersionId)?.content.resourceName;
  }

  async serve(page: Page) {
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname.replace(/^\/api/, "");
        const notFound = () => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
        if (path === "/observations") return route.fulfill({ json: { versions: [], next: null } });
        if (path === "/versions/read") {
          const { versionIds } = request.postDataJSON() as { versionIds: string[] };
          const results = versionIds.map((versionId) => ({ versionId, status: "deleted" }));
          return route.fulfill({ json: { results } });
        }
        if (path === "/entities") {
          const resourceName = url.searchParams.get("resourceName");
          const category = url.searchParams.get("category");
          if (resourceName && !this.versions.some((v) => v.content.resourceName === resourceName)) return notFound();
          const versions = this.versions.filter((v) =>
            resourceName ? this.resourceOf(v) === resourceName : !category || v.category === category,
          );
          return route.fulfill({ json: { versions, next: null } });
        }
        const [, kind, id, rest] = path.split("/");
        if (kind === "versions") {
          const found = this.versions.find((v) => v.versionId === id);
          return found ? route.fulfill({ json: found }) : notFound();
        }
        if (kind === "entities" && id) {
          const found = this.versions.find((v) => v.entityId === id);
          if (!found) return notFound();
          if (rest === "versions") return route.fulfill({ json: { versions: [found] } });
          if (rest === "referrers") {
            const referrers = this.versions
              .filter((v) => Object.values(v.content).includes(found.versionId))
              .map((version) => ({ version, references: [] }));
            return route.fulfill({ json: { referrers, next: null } });
          }
          return route.fulfill({ json: found });
        }
        return notFound();
      },
    );
  }
}

function seed() {
  const backend = new Backend();
  const observers = backend.schema("application-identity-observers", "应用识别 Observer");
  const installs = backend.schema("local-applications", "本机应用");
  const products = backend.schema("application-products", "应用产品");
  const memberships = backend.schema("application-memberships", "线索归属");
  for (const [clue, path] of [
    [qq, "/Applications/QQ.app/Contents/MacOS/QQ"],
    [intl, "/Applications/QQ Intl.app/Contents/MacOS/QQ"],
    [lark, "/Applications/Lark.app/Contents/MacOS/Lark"],
  ] as const)
    backend.add("business", installs.versionId, {
      identifiers: [clue, { scheme: "macos.executable-path", value: path }],
    });
  const observer = backend.add("business", observers.versionId, { programVersion: "1.0.0", model: "openai/test" });
  const product = backend.add("business", products.versionId, { name: "QQ" });
  backend.add("business", products.versionId, { name: "没有线索的产品" });
  const deleted = uuid();
  const clueOfQq = backend.add("business", memberships.versionId, {
    identifier: qq,
    productVersionId: product.versionId,
  });
  backend.add("business", memberships.versionId, { identifier: intl, productVersionId: product.versionId });
  backend.add("business", memberships.versionId, { identifier: lark, productVersionId: deleted });
  backend.add("observation", null, {
    observerVersionId: observer.versionId,
    contentVersionId: clueOfQq.versionId,
    inputSetVersionId: null,
    startAt: null,
    endAt: null,
    timeZone: null,
  });
  return backend;
}

test("产品按线索归属列出，指向已删除产品的线索未归属，没有线索归属的产品不列出", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/applications");
  await expect(page.getByText("1 个产品 · 3 个安装 · 1 条线索未归属")).toBeVisible();
  const row = page.getByRole("article", { name: "应用产品 QQ" });
  await expect(row.getByRole("button", { name: /com\.tencent\.qq\.intl/ })).toBeVisible();
  await expect(page.getByRole("article", { name: "应用产品 没有线索的产品" })).toHaveCount(0);
  await expect(page.getByRole("region", { name: "未归属" }).getByText("com.electron.lark")).toBeVisible();
});

test("产品面板列出判断记录，一条线索归属一行", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/applications");
  await page.getByRole("button", { name: "QQ", exact: true }).click();
  const records = page.getByRole("heading", { name: "判断记录" }).locator("xpath=ancestor::section[1]");
  await expect(records.getByRole("link")).toHaveCount(2);
  await expect(records.getByRole("link", { name: "com.tencent.qq", exact: true })).toBeVisible();
});

test("点线索打开它的线索归属", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/applications");
  await page.getByRole("button", { name: /com\.tencent\.qq$/ }).click();
  await expect(page).toHaveURL(/stack=/);
});

test("没有本机应用时的空状态", async ({ page }) => {
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } }),
  );
  await page.goto("/applications");
  await expect(page.getByText("还没有本机应用")).toBeVisible();
});
