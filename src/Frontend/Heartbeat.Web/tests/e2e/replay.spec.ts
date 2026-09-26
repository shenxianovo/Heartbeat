import { expect, test, type Page } from "@playwright/test";
import path from "node:path";

import {
  customTrack,
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

async function clickCurrentInputDensity(page: Page) {
  const curve = page.locator(".timeline-density-curve").first();
  await expect(curve.locator(".density-hit").first()).toBeAttached();
  await curve.focus();
  await curve.press("Enter");
}

test.beforeEach(async ({ page }) => {
  await seedSession(page);
  await identityRoutes(page);
});

test("深色主题首页水合时不报告 html 属性不一致", async ({ page }) => {
  const hydrationErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error" && message.text().includes("hydrated but some attributes")) {
      hydrationErrors.push(message.text());
    }
  });
  await page.addInitScript(() => localStorage.setItem("heartbeat-theme", "dark"));
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page.locator("html")).toHaveClass(/dark/);
  await expect(page.getByRole("button", { name: "刷新", exact: true })).toBeVisible();
  expect(hydrationErrors).toEqual([]);
});

test("统一时间线自动读取完整区间并可查看原始记录", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  await expect(
    page.getByRole("navigation", { name: "主导航" }).getByRole("link", { name: "Hub 管理" }),
  ).toBeVisible();
  await expect(page.getByLabel("当前登录：tester")).toContainText("T");
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toBeVisible();
  await expect(
    page.getByRole("button", { name: "com.microsoft.VSCode", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "时间区间", exact: true }).click();
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
  await clickCurrentInputDensity(page);
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
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toBeVisible();
});

test("一条 Track 失败时保留其余数据并可单独重试", async ({ page }) => {
  await recordingRoutes(page);
  let failDesktop = true;
  await page.route(`**/api/v1/tracks/${desktopTrack.id}/records?**`, async (route) => {
    if (failDesktop) {
      await route.fulfill({ status: 503, json: { title: "Track 暂时不可用" } });
      return;
    }
    await route.fallback();
  });

  await page.goto("/");
  const failureAlert = page.locator(".density-error");
  await expect(failureAlert).toContainText("1 条 Track 读取失败");
  await expect(page.getByText("观测状态", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toHaveCount(0);

  failDesktop = false;
  await page.getByRole("button", { name: "重试失败 Track" }).click();
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toBeVisible();
  await expect(failureAlert).toHaveCount(0);
});

test("部分 Track 失败而其余仍在加载时不误报全部失败", async ({ page }) => {
  await recordingRoutes(page);
  let failedRequests = 0;
  let release!: () => void;
  const waiting = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/api/v1/tracks", (route) =>
    route.fulfill({ json: { tracks: [desktopTrack, customTrack] } }),
  );
  await page.route(`**/api/v1/tracks/${desktopTrack.id}/records?**`, (route) => {
    failedRequests++;
    return route.fulfill({ status: 503, json: { title: "Track 暂时不可用" } });
  });
  await page.route(`**/api/v1/tracks/${customTrack.id}/point-counts?**`, async (route) => {
    await waiting;
    await route.fallback();
  });
  try {
    await page.goto("/");
    await expect.poll(() => failedRequests).toBe(3);
    await expect(page.getByText("正在构建统一时间窗口")).toBeVisible();
    await expect(page.getByText("所选 Track 均读取失败。")).toHaveCount(0);
    release();
    await expect(page.locator(".timeline-density-curve")).toBeVisible();
    await expect(page.locator(".density-error")).toContainText("1 条 Track 读取失败");
    await page.screenshot({ path: evidencePath("replay-partial-failure.png"), fullPage: true });
  } finally {
    release();
  }
});

test("退出后清理会话，返回首页不能继续查看缓存记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "退出登录" }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), userStorageKey)).toBeNull();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toHaveCount(0);
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
  await page.getByRole("button", { name: "全天", exact: true }).click();
  const first = page.getByRole("button", { name: "Overlap A", exact: true });
  const second = page.getByRole("button", { name: "Overlap B", exact: true });
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
  await expect(page.getByRole("button", { name: "com.apple.finder", exact: true })).toBeVisible();
  await expect(
    page.getByRole("button", { name: /example\.observation.*输入密度曲线/ }),
  ).toBeVisible();
});

test("概览拖选和手柄调整同步泳道，并按固定时间片查询更细的输入密度", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  await page.getByRole("button", { name: "全天", exact: true }).click();
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
    .poll(() => {
      const tiles = requests.filter(
        (url) =>
          url.pathname.endsWith("point-counts") &&
          Number(url.searchParams.get("bucketSeconds")) < 900,
      );
      return (
        tiles.length > 0 &&
        Math.min(...tiles.map((url) => Date.parse(url.searchParams.get("from")!))) <= start &&
        Math.max(...tiles.map((url) => Date.parse(url.searchParams.get("to")!))) >= end
      );
    })
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

test("密度曲线在细节读取时保持可见，复用同一时间片并可打开原始记录", async ({ page }) => {
  const requests = await recordingRoutes(page);
  let releaseFine!: () => void;
  const fineGate = new Promise<void>((resolve) => {
    releaseFine = resolve;
  });
  let waiting = 0;
  await page.route(`**/api/v1/tracks/${customTrack.id}/point-counts?**`, async (route) => {
    const bucketSeconds = Number(new URL(route.request().url()).searchParams.get("bucketSeconds"));
    if (bucketSeconds < 900) {
      waiting++;
      await fineGate;
    }
    await route.fallback();
  });
  await page.goto("/");
  await page.getByRole("button", { name: "全天", exact: true }).click();
  const curve = page.locator(".timeline-density-curve").first();
  await expect(curve.locator("path.density-wave").first()).toHaveAttribute("d", /M /);
  await expect(curve.locator("path.density-wave")).toHaveCount(1);
  const height = await curve.evaluate((element) => element.getBoundingClientRect().height);
  const start = Number(
    await page.getByRole("slider", { name: "范围起点" }).getAttribute("aria-valuenow"),
  );
  const end = Number(
    await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuenow"),
  );
  const eventAt = Date.now() - 30 * 60_000;
  const fullCurve = (await curve.boundingBox())!;
  await page.mouse.move(
    fullCurve.x + ((eventAt - start) / (end - start)) * fullCurve.width,
    fullCurve.y + fullCurve.height / 2,
  );
  await page.keyboard.down("Control");
  await page.mouse.wheel(0, -500);
  await page.keyboard.up("Control");
  await expect.poll(() => waiting).toBeGreaterThan(0);
  await expect(curve.locator("path.density-wave").first()).toHaveAttribute("d", /M /);
  expect(await curve.evaluate((element) => element.getBoundingClientRect().height)).toBe(height);
  const coarseSeconds = Number(await curve.getAttribute("data-source-seconds"));
  releaseFine();
  await expect
    .poll(async () => Number(await curve.getAttribute("data-source-seconds")))
    .toBeLessThan(coarseSeconds);

  const fineRequests = () =>
    requests.filter(
      (url) =>
        url.pathname.endsWith("point-counts") &&
        Number(url.searchParams.get("bucketSeconds")) < 900,
    ).length;
  await expect.poll(fineRequests).toBe(waiting);
  const beforePan = fineRequests();
  const plot = (await page.locator(".timeline-lane-plot").first().boundingBox())!;
  await page.mouse.move(plot.x + plot.width / 2, plot.y + plot.height / 2);
  await page.mouse.wheel(5, 0);
  await page.waitForTimeout(250);
  expect(fineRequests()).toBe(beforePan);

  await page.getByRole("button", { name: "全天", exact: true }).click();
  await clickCurrentInputDensity(page);
  await expect(page.getByRole("button", { name: "查看详情" })).toBeVisible();
  await page.getByRole("button", { name: "关闭详情" }).click();
  await curve.focus();
  await page.keyboard.press("ArrowRight");
  await expect(curve.locator(".density-keyboard-marker")).toHaveCount(1);
  await page.keyboard.press("Enter");
  await expect(page.locator(".point-details")).toBeVisible();
});

test("应用子泳道与记录详情联动，日期切换清理选中记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByRole("button", { name: "展开 测试 Mac 的应用" }).click();
  const app = page
    .locator(".application-sublane")
    .filter({ has: page.getByText("com.apple.finder", { exact: true }) });
  await expect(app).toBeVisible();
  await app.getByRole("button", { name: "com.apple.finder", exact: true }).click();
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
  await page.getByRole("button", { name: "全天", exact: true }).click();
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
  const startBeforePan = Number(await start.getAttribute("aria-valuenow"));
  await page.mouse.wheel(160, 0);
  await expect
    .poll(async () => Number(await start.getAttribute("aria-valuenow")))
    .toBeGreaterThan(startBeforePan);
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
  await page.locator("main").click({ position: { x: 5, y: 5 } });
  await expect(picker).toHaveCount(0);
  await expect(trigger).toContainText("测试 Mac");
  await page.getByRole("button", { name: "自定义时间范围" }).click();
  await expect(page.getByRole("dialog", { name: "自定义时间范围" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});

test("泳道悬停提示是跟随指针的浮层，离开即收起", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByRole("button", { name: "全天", exact: true }).click();
  const tip = page.locator(".ui-tooltip");
  const curve = page.locator(".timeline-density-curve").first();
  await expect(curve).toBeVisible();
  await expect(tip).toHaveCount(0);

  // 空白处也读得出：曲线画的是 0，浮层就得说这段没记录，而不是什么都不弹。
  const plot = (await curve.boundingBox())!;
  await page.mouse.move(plot.x + plot.width * 0.2, plot.y + plot.height / 2);
  await expect(tip).toBeVisible();
  await expect(tip).toContainText("没有记录");
  expect(await tip.evaluate((element) => getComputedStyle(element).pointerEvents)).toBe("none");
  const first = (await tip.boundingBox())!;

  // 有记录的桶读出真实条数，并且浮层跟着指针走。
  const start = Number(
    await page.getByRole("slider", { name: "范围起点" }).getAttribute("aria-valuenow"),
  );
  const end = Number(
    await page.getByRole("slider", { name: "范围终点" }).getAttribute("aria-valuenow"),
  );
  const at = Date.now() - 30 * 60_000;
  await page.mouse.move(
    plot.x + ((at - start) / (end - start)) * plot.width,
    plot.y + plot.height / 2,
  );
  await expect(tip).toContainText("1 条");
  const moved = (await tip.boundingBox())!;
  expect(moved.x).toBeGreaterThan(first.x);

  // 区间段：同一个浮层复用，同时只会有一个。
  const range = page.getByRole("button", { name: "com.apple.finder", exact: true }).first();
  await range.hover();
  await expect(tip).toHaveCount(1);
  await expect(tip).toContainText("com.apple.finder");
  await page.screenshot({ path: evidencePath("replay-hover-tip.png") });

  await page.getByRole("button", { name: "刷新", exact: true }).hover();
  await expect(tip).toHaveCount(0);

  // 在窄屏顶部和右侧触发真实布局，定位方式改变也不影响这些行为断言。
  await page.setViewportSize({ width: 390, height: 300 });
  await curve.evaluate((element) => element.scrollIntoView({ block: "start" }));
  const edge = (await curve.boundingBox())!;
  const pointer = { x: edge.x + edge.width - 2, y: Math.max(2, edge.y + 2) };
  await page.mouse.move(pointer.x, pointer.y);
  await expect(tip).toBeVisible();
  const box = (await tip.boundingBox())!;
  expect(pointer.y).toBeLessThan(box.height);
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  expect(box.y + box.height).toBeLessThanOrEqual(300);
  expect(
    pointer.x < box.x ||
      pointer.x > box.x + box.width ||
      pointer.y < box.y ||
      pointer.y > box.y + box.height,
  ).toBe(true);
  await page.screenshot({ path: evidencePath("replay-hover-tip-edge.png") });
});

test("现在自动跟随；用户操作和刷新保留视窗，回到现在才恢复", async ({ page }) => {
  await page.clock.install();
  await recordingRoutes(page);
  await page.goto("/");
  const start = page.getByRole("slider", { name: "范围起点" });
  const end = page.getByRole("slider", { name: "范围终点" });
  await expect(start).toBeVisible();
  const readStart = async () => Number(await start.getAttribute("aria-valuenow"));
  const initial = await readStart();
  expect(Number(await end.getAttribute("aria-valuenow")) - initial).toBe(2 * 60 * 60_000);
  await page.clock.fastForward(30_000);
  await expect.poll(readStart).toBeGreaterThan(initial);

  // Pointer-down alone must freeze the view before a drag has moved any pixels.
  const plot = (await page.locator(".timeline-lane-plot").first().boundingBox())!;
  await page.mouse.move(plot.x + plot.width / 2, plot.y + 3);
  await page.mouse.down();
  const held = await readStart();
  await page.clock.fastForward(30_000);
  expect(await readStart()).toBe(held);
  await page.mouse.move(plot.x + plot.width * 0.7, plot.y + 3, { steps: 5 });
  await page.mouse.up();
  const manual = await readStart();
  expect(manual).toBeLessThan(held);
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.clock.fastForward(30_000);
  expect(await readStart()).toBe(manual);

  await page.getByRole("button", { name: "回到现在", exact: true }).click();
  await expect.poll(readStart).toBeGreaterThan(held);
  const resumed = await readStart();
  await page.clock.fastForward(30_000);
  await expect.poll(readStart).toBeGreaterThan(resumed);
  await page.getByRole("button", { name: "com.apple.finder", exact: true }).click();
  const inspecting = await readStart();
  await page.clock.fastForward(30_000);
  expect(await readStart()).toBe(inspecting);
  await expect(page.getByRole("region", { name: "所选记录详情" })).toBeVisible();
  await page.screenshot({ path: evidencePath("replay-follow-paused.png"), fullPage: true });
});

test("历史日期在完整读取后聚焦首条活动，刷新不会重新定位", async ({ page }) => {
  await recordingRoutes(page);
  await page.route("**/api/v1/tracks", (route) =>
    route.fulfill({ json: { tracks: [desktopTrack] } }),
  );
  await page.route(`**/api/v1/tracks/${desktopTrack.id}/records?**`, async (route) => {
    const from = Date.parse(new URL(route.request().url()).searchParams.get("from")!);
    const at = new Date(from + 4 * 3_600_000).toISOString();
    await route.fulfill({
      json: {
        track: desktopTrack,
        records: [
          {
            id: "early-activity",
            startedAt: at,
            endedAt: new Date(from + 5 * 3_600_000).toISOString(),
            observedAt: null,
            receivedAt: at,
            value: {},
          },
        ],
        nextCursor: null,
      },
    });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "前一天", exact: true }).click();
  const start = page.getByRole("slider", { name: "范围起点" });
  const dayStart = Number(await start.getAttribute("aria-valuemin"));
  await expect(start).toHaveAttribute("aria-valuenow", String(dayStart + 3 * 3_600_000));
  await page.getByRole("button", { name: "向后平移", exact: true }).click();
  const manual = await start.getAttribute("aria-valuenow");
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await expect(page.getByRole("button", { name: "刷新", exact: true })).toBeEnabled();
  await expect(start).toHaveAttribute("aria-valuenow", manual!);
  await page.screenshot({ path: evidencePath("replay-history-focus.png"), fullPage: true });
});
