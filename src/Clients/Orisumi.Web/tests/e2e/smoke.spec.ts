import { expect, test, type Locator, type Page } from "@playwright/test";

// 默认的 e2e 不连后端：用演示数据源检查视图和交互，用路由拦截检查空状态。

/** 视图里的元素每帧都会替换，按坐标点击，不等元素稳定。 */
async function clickAt(page: Page, target: Locator) {
  let box: Awaited<ReturnType<Locator["boundingBox"]>> = null;
  await expect
    .poll(async () => {
      box = await target
        .first()
        .boundingBox({ timeout: 1000 })
        .catch(() => null);
      return box !== null;
    })
    .toBe(true);
  const { x, y, width, height } = box!;
  await page.mouse.click(x + Math.min(width / 2, 40), y + height / 2);
}

function view(page: Page) {
  return page.getByRole("img", { name: /写入|Observation/ });
}

test.describe("现在 · 演示数据", () => {
  test("四种视图都能渲染", async ({ page }) => {
    await page.goto("/?source=demo");
    await expect(page.getByText("演示数据")).toBeVisible();
    await expect(page.getByText(/\d+ 个 Observer，\d+ 个正在写入/)).toBeVisible();
    await expect(page.locator('svg[data-view="ecg"] path').first()).toBeAttached();

    for (const [label, key] of [
      ["气泡", "bubble"],
      ["行", "strip"],
      ["表盘", "ring"],
    ] as const) {
      await page.getByRole("button", { name: label, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`view=${key}`));
      await expect(page.locator(`svg[data-view="${key}"] [data-key]`).first()).toBeAttached();
    }
    await expect(page.locator('svg[data-view="ring"]')).toContainText("按事实时间");
  });

  test("切换视图后选中保持，刷新后恢复", async ({ page }) => {
    await page.goto("/?source=demo&view=strip");
    const row = page.locator('svg[data-view="strip"] [data-key]').first();
    await expect(row).toBeAttached();
    const name = (await row.locator("title").textContent()) ?? "";
    await clickAt(page, row);
    await expect(page).toHaveURL(/observer=/);

    const footer = page.locator("footer");
    await expect(footer.getByRole("link", { name: "打开实体" })).toBeVisible();
    await expect(footer).toContainText(name);

    await page.getByRole("button", { name: "心电", exact: true }).click();
    await expect(page).toHaveURL(/view=ecg.*observer=|observer=.*view=ecg/);
    await expect(footer).toContainText(name);
    await expect(view(page)).toContainText(`每分钟写入 · ${name}`);

    // 演示数据每次加载都重新生成，刷新后只检查 URL 中的视图和选中都还在。
    const selected = new URL(page.url()).searchParams.get("observer");
    await page.reload();
    expect(new URL(page.url()).searchParams.get("observer")).toBe(selected);
    await expect(page.getByRole("button", { name: "心电", exact: true })).toHaveAttribute("aria-pressed", "true");
  });

  test("从推断沿引用链看到输入版本和用户的否定", async ({ page }) => {
    await page.goto("/?source=demo&view=strip");
    await expect(page.locator('svg[data-view="strip"] [data-key]').first()).toBeAttached();
    await clickAt(page, page.locator("svg [data-more]"));
    const inference = page.locator('svg[data-view="strip"] [data-key]').filter({ hasText: "活动推断" });
    await expect(inference).toBeAttached();
    await clickAt(page, inference);
    await page.locator("footer").getByRole("link", { name: "打开实体" }).click();

    await expect(page.getByRole("heading", { name: "活动推断" })).toBeVisible();
    await page.getByRole("list", { name: "被引用" }).getByRole("link").first().click();

    await expect(page.getByRole("heading", { name: "Observation" })).toBeVisible();
    await page.getByRole("button", { name: /InputSet · 3/ }).click();
    const inputs = page.getByRole("list", { name: "InputSet 的输入" });
    await expect(inputs).toContainText("v1 / v2");
    await expect(inputs).toContainText("不可读取");
    const referrers = page.getByRole("list", { name: "被引用" });
    await expect(referrers).toContainText("陈述");

    // 删除用户的陈述后回到推断，它不再出现在被引用中。
    const observationUrl = page.url();
    await referrers.getByRole("link", { name: /陈述/ }).click();
    await expect(page.getByRole("heading", { name: "陈述" })).toBeVisible();
    await page.getByRole("button", { name: "删除" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "删除" }).click();
    await expect(page).toHaveURL(observationUrl);
    await expect(page.getByText("没有其他版本引用它")).toBeVisible();
  });

  test("仍在使用的 EntitySchema 不能删除", async ({ page }) => {
    await page.goto("/schemas?source=demo");
    await page
      .getByRole("list", { name: "Schema" })
      .getByRole("link", { name: /demo-statements/ })
      .click();
    const url = page.url();
    await page.getByRole("button", { name: "删除" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "删除" }).click();
    await expect(page.getByRole("dialog").getByRole("alert")).toContainText("还有业务版本使用这个 EntitySchema");
    await page.getByRole("dialog").getByRole("button", { name: "取消" }).click();
    await expect(page).toHaveURL(url);
  });
});

test.describe("外壳", () => {
  test("主题切换写到 html 上", async ({ page }) => {
    await page.goto("/?source=demo");
    await page.getByRole("button", { name: "深", exact: true }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.getByRole("button", { name: "系统", exact: true }).click();
    await expect(page.locator("html")).not.toHaveAttribute("data-theme");
  });

  test("ID 输入框检查格式，找不到时提示", async ({ page }) => {
    await page.goto("/?source=demo");
    const input = page.getByLabel("EntityId 或 VersionId");
    await input.fill("hello");
    await input.press("Enter");
    await expect(page.getByRole("alert")).toHaveText("不是 UUIDv7");
    await input.fill("0192b056-7300-7000-8000-000000000001");
    await input.press("Enter");
    await expect(page.getByRole("alert")).toHaveText("找不到这个 ID");
  });

  test("今天没有 Observation 时显示空状态", async ({ page }) => {
    await page.route("**/api/observations?**", (route) =>
      route.fulfill({ json: { versions: [], next: null }, contentType: "application/json" }),
    );
    await page.goto("/?source=api");
    await expect(page.getByText("今天还没有记录")).toBeVisible();
    await expect(page.getByText("0 个 Observer，0 个正在写入")).toBeVisible();
  });
});
