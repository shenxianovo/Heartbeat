import { expect, test } from "@playwright/test";

test.describe("Heartbeat 观测台 · 冒烟", () => {
  test("首页渲染品牌、搜索栏与三个特性卡片", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /从一次/ })).toBeVisible();
    await expect(page.getByRole("main").getByRole("button", { name: "观测" })).toBeDisabled();
    await expect(page.getByRole("button", { name: /Windows 娘主题/ })).toBeVisible();
    await expect(page.getByRole("heading", { name: "引用关系图", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "实体检视", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "事实时间", exact: true })).toBeVisible();
  });

  test("输入合法 UUIDv7 后启用观测并跳转实体页", async ({ page }) => {
    await page.goto("/");
    const main = page.getByRole("main");
    await main.getByLabel("实体 ID").fill("0192b056-7300-7000-8000-000000000001");
    await expect(main.getByRole("button", { name: "观测" })).toBeEnabled();
    await main.getByRole("button", { name: "观测" }).click();
    // 后端未运行时进入实体页并展示错误卡（而不是白屏）
    await expect(page).toHaveURL(/\/entities\/0192b056-7300-7000-8000-000000000001/);
    await expect(page.getByRole("heading", { name: "无法读取这个实体" })).toBeVisible({ timeout: 15_000 });
  });

  test("双生主题切换：吉祥物头像切换 data-mascot，明暗切换 dark 类", async ({ page }) => {
    await page.goto("/");
    const root = page.locator("html");
    await page.getByRole("button", { name: "切换到Mac 娘主题" }).click();
    await expect(root).toHaveAttribute("data-mascot", "mac");
    await page.getByRole("button", { name: "切换到Windows 娘主题" }).click();
    await expect(root).toHaveAttribute("data-mascot", "windows");

    const currentDark = await root.evaluate((el) => el.classList.contains("dark"));
    await page.getByRole("button", { name: "切换明暗模式" }).click();
    await expect.poll(async () => root.evaluate((el) => el.classList.contains("dark"))).toBe(!currentDark);
  });

  test("写入页默认契约校验失败，生成 UUIDv7 后通过", async ({ page }) => {
    await page.goto("/compose");
    await expect(page.getByText(/不是合法 UUIDv7/)).toBeVisible();
    await page.getByRole("button", { name: "生成新 ID" }).click();
    await expect(page.getByText("契约检查通过，可以提交")).toBeVisible();
    // 预览徽章里能看到目标路径
    await expect(page.getByText(/^PUT \/api\/entities\/observers\//)).toBeVisible();
  });
});
