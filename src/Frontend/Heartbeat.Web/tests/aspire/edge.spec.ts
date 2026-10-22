import { expect, test } from "@playwright/test";

// 经 Aspire/nginx 边缘入口（8081）验证 Web 资源：页面、同源 API、Vite HMR WebSocket。
const BASE = "http://localhost:18081";

test("web edge: page loads, same-origin API works, HMR websocket opens", async ({ page }) => {
  const wsUrls: string[] = [];
  const consoleErrors: string[] = [];
  page.on("websocket", (ws) => wsUrls.push(ws.url()));
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });

  await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: /从一次/ })).toBeVisible();

  // Vite HMR 客户端会建立 WebSocket（经 nginx 的 upgrade 代理）。
  await expect.poll(() => wsUrls.some((u) => u.includes("/")), { timeout: 10_000 }).toBe(true);
  expect(wsUrls.some((u) => u.startsWith("ws://localhost:18081"))).toBe(true);

  // 同源 /api：不存在实体返回 404 应用错误格式（经 nginx 转 API）。
  const res = await page.request.get(`${BASE}/api/entities/0192b056-7300-7000-8000-0000000000ff`);
  expect(res.status()).toBe(404);

  // 深链 SPA 回退
  await page.goto(`${BASE}/compose`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: "写入实体" })).toBeVisible();

  // Vite 自身 HMR 连接失败会打 console error（连接关闭等业务噪声除外）
  const viteWsErrors = consoleErrors.filter((t) => /WebSocket|vite/i.test(t));
  expect(viteWsErrors).toEqual([]);
});
