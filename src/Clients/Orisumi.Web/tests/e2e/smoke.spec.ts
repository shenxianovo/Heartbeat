import { expect, test, type Page } from "@playwright/test";

// 默认的 e2e 不连后端：用路由拦截给出空的列表和 404，检查外壳和空状态。真实数据下的视图和实体页由 tests/aspire/edge.spec.ts 检查。

const EMPTY_PAGE = { versions: [], next: null };

/** 列表接口返回空页，其余读取返回 404。 */
async function emptyApi(page: Page) {
  // 只拦截 /api 下的请求；Vite 的 /src/api/*.ts 模块照常加载。
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => {
      const { pathname } = new URL(route.request().url());
      if (pathname === "/api/observations" || pathname === "/api/entities") {
        return route.fulfill({ json: EMPTY_PAGE });
      }
      if (pathname === "/api/observation-previews") return route.fulfill({ json: { previews: [] } });
      return route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
    },
  );
}

test.beforeEach(async ({ page }) => {
  await emptyApi(page);
});

test.describe("外壳", () => {
  test("主题切换写到 html 上", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("button", { name: "深", exact: true }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.getByRole("button", { name: "系统", exact: true }).click();
    await expect(page.locator("html")).not.toHaveAttribute("data-theme");
  });

  test("ID 输入框检查格式，找不到时提示", async ({ page }) => {
    await page.goto("/");
    const input = page.getByLabel("EntityId 或 VersionId");
    await input.fill("hello");
    await input.press("Enter");
    await expect(page.getByRole("alert")).toHaveText("不是 UUIDv7");
    await input.fill("0192b056-7300-7000-8000-000000000001");
    await input.press("Enter");
    await expect(page.getByRole("alert")).toHaveText("找不到这个 ID");
  });
});

test.describe("空状态", () => {
  test("日页面今天没有 Observation", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByText("今天还没有记录")).toBeVisible();
  });

  test("Observer 页面还没有 Observer", async ({ page }) => {
    await page.goto("/observers");
    await expect(page.getByText("还没有 Observer")).toBeVisible();
    await expect(page.getByText("0 个 Observer，0 个正在写入")).toBeVisible();
  });
});
