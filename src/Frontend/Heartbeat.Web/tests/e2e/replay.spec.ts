import { expect, test } from "@playwright/test";

import {
  customTrack,
  identityRoutes,
  recordingRoutes,
  seedSession,
  userStorageKey,
} from "./fixtures";

test.beforeEach(async ({ page }) => {
  await seedSession(page);
  await identityRoutes(page);
});

test("回放展示应用、隔离坏数据，并保留筛选条件加载下一页", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
  await expect(page.getByText(/malformed-observation/)).toBeVisible();
  await page.getByRole("button", { name: "查看详情" }).first().click();
  await expect(page.getByText("原始 JSON", { exact: true }).first()).toBeVisible();
  await page.getByRole("button", { name: "加载更多记录" }).click();
  await expect(page.getByText("com.microsoft.VSCode", { exact: true })).toBeVisible();
  const first = requests.find((url) => url.pathname.endsWith("/records"))!;
  const next = requests.find((url) => url.searchParams.has("cursor"))!;
  expect(next.searchParams.get("cursor")).toBe("next-page-test-cursor");
  expect(next.searchParams.get("from")).toBe(first.searchParams.get("from"));
  expect(next.searchParams.get("to")).toBe(first.searchParams.get("to"));
  await expect(page.getByRole("button", { name: "加载更多记录" })).toHaveCount(0);
  await page.screenshot({ path: "test-results/replay-desktop.png", fullPage: true });
});

test("未知类型安全展示 JSON，窄屏也可查看", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByRole("combobox").selectOption(customTrack.id);
  await expect(page.getByText(/<script>window.untrustedExecuted=true<\/script>/)).toBeVisible();
  await expect(page.getByText("瞬时记录", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => "untrustedExecuted" in window)).toBe(false);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path: "test-results/replay-mobile.png", fullPage: true });
});

test("空目录有明确状态", async ({ page }) => {
  await recordingRoutes(page, { empty: true });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "还没有可以回放的记录" })).toBeVisible();
});

test("请求失败后可重试恢复", async ({ page }) => {
  await recordingRoutes(page, { fail: true });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "暂时无法读取采集来源" })).toBeVisible({
    timeout: 15000,
  });
  await recordingRoutes(page);
  await page.getByRole("button", { name: "重试" }).click();
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
});

test("退出后清理会话，返回首页不能继续查看缓存记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "退出登录" }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), userStorageKey)).toBeNull();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByText("com.apple.finder", { exact: true })).toHaveCount(0);
});
