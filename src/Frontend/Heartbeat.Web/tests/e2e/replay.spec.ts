import { expect, test, type Page } from "@playwright/test";
import path from "node:path";

import {
  desktopTrack,
  identityRoutes,
  recordingRoutes,
  seedSession,
  userStorageKey,
} from "./fixtures";

const evidencePath = (name: string) =>
  path.join(process.env.HEARTBEAT_EVIDENCE_DIR ?? "test-results", name);

async function chooseSources(page: Page, names: string[]) {
  await page.getByRole("button", { name: "采集来源", exact: true }).click();
  const picker = page.getByRole("dialog", { name: "采集来源", exact: true });
  await picker.getByRole("button", { name: "清空", exact: true }).click();
  for (const name of names) await picker.getByRole("checkbox", { name, exact: true }).check();
  await picker.getByRole("button", { name: "完成", exact: true }).click();
}

test.beforeEach(async ({ page }) => {
  await seedSession(page);
  await identityRoutes(page);
});

test("统一时间线自动读取完整区间并可查看原始记录", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toBeVisible();
  await expect(page.getByTitle("com.microsoft.VSCode", { exact: true })).toBeVisible();
  await page.getByTitle("时间区间", { exact: true }).click();
  await page.getByRole("button", { name: "查看详情" }).click();
  await expect(page.getByText(/malformed-observation/).first()).toBeVisible();
  await expect(page.getByText("原始 JSON", { exact: true }).first()).toBeVisible();
  const first = requests.find((url) => url.pathname.endsWith("/records"))!;
  const next = requests.find((url) => url.searchParams.has("cursor"))!;
  expect(next.searchParams.get("cursor")).toBe("next-page-test-cursor");
  expect(next.searchParams.get("from")).toBe(first.searchParams.get("from"));
  expect(next.searchParams.get("to")).toBe(first.searchParams.get("to"));
  await page.screenshot({ path: evidencePath("replay-desktop.png"), fullPage: true });
});

test("未知类型安全展示 JSON，窄屏也可查看", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await recordingRoutes(page);
  await page.goto("/");
  await chooseSources(page, ["自定义来源"]);
  await page.getByRole("button", { name: /example\.observation.*1 条记录/ }).click();
  await expect(page.getByText(/<script>window.untrustedExecuted=true<\/script>/)).toBeVisible();
  await expect(page.getByText("瞬时记录", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => "untrustedExecuted" in window)).toBe(false);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  const tickBoxes = await page.locator(".timeline-ruler span").evaluateAll((elements) =>
    elements.map((element) => {
      const box = element.getBoundingClientRect();
      return { left: box.left, right: box.right };
    }),
  );
  for (let index = 1; index < tickBoxes.length; index++)
    expect(tickBoxes[index - 1]!.right).toBeLessThanOrEqual(tickBoxes[index]!.left);
  await page.screenshot({ path: evidencePath("replay-mobile.png"), fullPage: true });
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
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toBeVisible();
});

test("退出后清理会话，返回首页不能继续查看缓存记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "退出登录" }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), userStorageKey)).toBeNull();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toHaveCount(0);
});

test("重叠区间分别可见且可以直接展开", async ({ page }) => {
  await recordingRoutes(page);
  await page.route(`**/api/v1/tracks/${desktopTrack.id}/records?**`, async (route) => {
    const from = Date.parse(new URL(route.request().url()).searchParams.get("from")!);
    const records = ["Overlap A", "Overlap B"].map((name, index) => ({
      id: `overlap-${index}`,
      startedAt: new Date(from + 4 * 3_600_000).toISOString(),
      endedAt: new Date(from + 8 * 3_600_000).toISOString(),
      observedAt: null,
      receivedAt: new Date(from + 8 * 3_600_000).toISOString(),
      value: {
        device_id: "test-mac",
        application: { platform: "macos", id_kind: "bundle_id", id: name },
      },
    }));
    await route.fulfill({ json: { track: desktopTrack, records, nextCursor: null } });
  });
  await page.goto("/");
  const first = page.getByTitle("Overlap A", { exact: true });
  const second = page.getByTitle("Overlap B", { exact: true });
  await expect(first).toBeVisible();
  await expect(second).toBeVisible();
  const a = (await first.boundingBox())!;
  const b = (await second.boundingBox())!;
  expect(a.y + a.height <= b.y || b.y + b.height <= a.y).toBe(true);
  await first.click();
  await expect(page.getByText("所选区间")).toBeVisible();
  await page.screenshot({ path: evidencePath("replay-overlapping.png"), fullPage: true });
});

test("来源可以组合选择，空选择不会一直加载", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await chooseSources(page, []);
  await expect(page.getByRole("heading", { name: "尚未选择来源" })).toBeVisible();
  await chooseSources(page, ["测试 Mac", "自定义来源"]);
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /example\.observation.*1 条记录/ })).toBeVisible();
});

test("概览拖选和手柄调整同步泳道，并按当前窗口查询更细的输入密度", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  const move = page.getByRole("slider", { name: "移动时间范围" });
  await expect(move).toBeVisible();
  const dayStart = Number(await move.getAttribute("aria-valuemin"));
  const dayEnd = Number(
    await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuemax"),
  );
  const overview = (await page.getByTestId("activity-overview").boundingBox())!;
  await page.mouse.move(overview.x + overview.width * 0.2, overview.y + overview.height / 2);
  await page.mouse.down();
  await page.mouse.move(overview.x + overview.width * 0.6, overview.y + overview.height / 2, {
    steps: 8,
  });
  await page.mouse.up();
  const start = Number(await move.getAttribute("aria-valuenow"));
  const end = Number(
    await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuenow"),
  );
  expect(start).toBeGreaterThan(dayStart);
  expect(end).toBeLessThan(dayEnd);
  await expect
    .poll(() =>
      requests.some(
        (url) =>
          url.pathname.endsWith("point-counts") &&
          Date.parse(url.searchParams.get("from")!) === start &&
          Date.parse(url.searchParams.get("to")!) === end &&
          Number(url.searchParams.get("bucketSeconds")) < 900,
      ),
    )
    .toBe(true);
  const rangeRequests = requests.filter(
    (url) => url.pathname.endsWith("/records") && url.pathname.includes(desktopTrack.id),
  );
  expect(rangeRequests).toHaveLength(2);
  const handle = page.getByRole("slider", { name: "范围起点" });
  const box = (await handle.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + 40, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  expect(Number(await handle.getAttribute("aria-valuenow"))).toBeGreaterThan(start);
  await page.getByRole("button", { name: "全天", exact: true }).click();
  expect(Number(await handle.getAttribute("aria-valuenow"))).toBe(dayStart);
  expect(
    Number(await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuenow")),
  ).toBe(dayEnd);
});

test("应用子泳道与记录详情联动，日期切换清理选中记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByRole("button", { name: "展开 测试 Mac 的应用" }).click();
  const app = page
    .locator(".application-sublane")
    .filter({ has: page.getByText("com.apple.finder", { exact: true }) });
  await expect(app).toBeVisible();
  await app.getByTitle("com.apple.finder", { exact: true }).click();
  await expect(page.getByRole("region", { name: "所选记录详情" })).toBeVisible();
  await page.getByRole("button", { name: "聚焦此记录" }).click();
  const span =
    Number(await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuenow")) -
    Number(await page.getByRole("slider", { name: "范围起点" }).getAttribute("aria-valuenow"));
  expect(span).toBeLessThan(10 * 60_000);
  await expect(page.getByText("正在读取密度…")).toHaveCount(0);
  await page.screenshot({ path: "test-results/experience-expanded.png", fullPage: true });
  await page.getByRole("button", { name: "前一天" }).click();
  await expect(page.getByRole("region", { name: "所选记录详情" })).toHaveCount(0);
});

test("观测状态与其他 Track 分开展示", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");

  const timeline = page.getByRole("region", { name: "活动泳道，方向键平移，加减键缩放" });
  await expect(timeline.getByText("前台应用", { exact: true })).toBeVisible();
  await expect(timeline.getByText("观测状态", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "区间记录列表" })).toContainText("观测状态");

  const warning = page.getByRole("button", {
    name: "应用观测 · 缺少权限 · screen-recording",
  });
  await expect(warning).toBeVisible();
  await warning.click();
  await expect(page.getByRole("region", { name: "所选记录详情" })).toContainText(
    "应用观测 · 缺少权限",
  );
  await page.screenshot({ path: evidencePath("replay-observation-track.png"), fullPage: true });
});

test("泳道滚动、平移缩放与键盘范围控制始终保持在日期边界内", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  const start = page.getByRole("slider", { name: "范围起点" });
  const end = page.getByRole("slider", { name: "范围终点" });
  await expect(start).toBeVisible();
  const dayStart = Number(await start.getAttribute("aria-valuenow"));
  const dayEnd = Number(await end.getAttribute("aria-valuenow"));
  await page.getByRole("button", { name: "放大时间轴" }).click();
  const initial = Number(await start.getAttribute("aria-valuenow"));
  const plot = (await page.locator(".timeline-lane-plot").first().boundingBox())!;
  await page.mouse.move(plot.x + plot.width * 0.5, plot.y + 35);
  await page.mouse.down();
  await page.mouse.move(plot.x + plot.width * 0.7, plot.y + 35, { steps: 5 });
  await page.mouse.up();
  expect(Number(await start.getAttribute("aria-valuenow"))).toBeLessThan(initial);
  expect(
    Number(await end.getAttribute("aria-valuenow")) -
      Number(await start.getAttribute("aria-valuenow")),
  ).toBe((dayEnd - dayStart) / 2);
  await start.focus();
  await page.keyboard.press("Home");
  expect(Number(await start.getAttribute("aria-valuenow"))).toBe(dayStart);
  await end.focus();
  await page.keyboard.press("End");
  expect(Number(await end.getAttribute("aria-valuenow"))).toBe(dayEnd);
  await page.addStyleTag({ content: ".swimlane-scroll { max-height: 80px !important; }" });
  const scroller = page.locator(".swimlane-scroll");
  expect(await scroller.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(
    true,
  );
  const visiblePlot = (await page.locator(".timeline-lane-plot").first().boundingBox())!;
  await page.mouse.move(visiblePlot.x + visiblePlot.width / 2, visiblePlot.y + 5);
  const spanBeforeWheel =
    Number(await end.getAttribute("aria-valuenow")) -
    Number(await start.getAttribute("aria-valuenow"));
  await page.mouse.wheel(0, 180);
  await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  expect(
    Number(await end.getAttribute("aria-valuenow")) -
      Number(await start.getAttribute("aria-valuenow")),
  ).toBe(spanBeforeWheel);
  await page.keyboard.down("Control");
  await page.mouse.wheel(0, -180);
  await page.keyboard.up("Control");
  await expect
    .poll(
      async () =>
        Number(await end.getAttribute("aria-valuenow")) -
        Number(await start.getAttribute("aria-valuenow")),
    )
    .toBeLessThan(dayEnd - dayStart);
});

test("共享日期选择器支持月切换、键盘选择、今天和 Escape 关闭", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  const trigger = page.getByRole("button", { name: "选择日期", exact: true });
  await trigger.click();
  const calendar = page.getByRole("dialog", { name: "选择日期", exact: true });
  await expect(calendar).toBeVisible();
  const selected = calendar.locator('.calendar-day[aria-pressed="true"]');
  const current = (await selected.getAttribute("aria-label"))!;
  await expect(selected).toBeFocused();
  await page.keyboard.press("ArrowLeft");
  const previous = await page.locator(":focus").getAttribute("aria-label");
  expect(previous).not.toBe(current);
  await page.keyboard.press("Enter");
  await expect(calendar).toHaveCount(0);
  await expect(trigger).toContainText(previous!);
  await expect(trigger).toBeFocused();
  await trigger.click();
  await calendar.getByRole("button", { name: "上个月" }).click();
  await page.screenshot({ path: "test-results/main-calendar.png" });
  await page.keyboard.press("Escape");
  await expect(calendar).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await trigger.click();
  await calendar.getByRole("button", { name: "今天", exact: true }).click();
  await expect(trigger).toContainText(current);
});

test("Picker 浮层窄屏可用，点击外部关闭且多选保持", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await recordingRoutes(page);
  await page.goto("/");
  await chooseSources(page, ["测试 Mac"]);
  const trigger = page.getByRole("button", { name: "采集来源", exact: true });
  await expect(trigger).toContainText("测试 Mac");
  await trigger.click();
  const picker = page.getByRole("dialog", { name: "采集来源" });
  await expect(picker.getByRole("checkbox", { name: "测试 Mac", exact: true })).toBeChecked();
  await expect(picker.getByRole("checkbox", { name: "自定义来源", exact: true })).not.toBeChecked();
  const box = (await picker.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: "test-results/main-picker-mobile.png", fullPage: true });
  await page.getByRole("heading", { name: "当天经历" }).click();
  await expect(picker).toHaveCount(0);
  await expect(trigger).toContainText("测试 Mac");
  await page.getByRole("button", { name: "自定义时间范围" }).click();
  await expect(page.getByRole("dialog", { name: "自定义时间范围" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});
