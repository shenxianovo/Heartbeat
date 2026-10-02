import { desktopPath, desktopObject, deviceReference, appReference } from "./fixtures";
import { expect, test, type Page, type Locator } from "@playwright/test";
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

async function clickCurrentInputDensity(page: Page) {
  const curve = page.locator(".timeline-density-curve").first();
  await expect
    .poll(async () => Number(await curve.getAttribute("data-buckets")))
    .toBeGreaterThan(0);
  await expect.poll(() => hasInk(curve)).toBe(true);
  await curve.focus();
  await curve.press("Enter");
}

const pageErrors = new WeakMap<Page, string[]>();
test.afterEach(({ page }) => {
  expect(pageErrors.get(page) ?? []).toEqual([]);
});

/** Read actual pixels, not the React wrapper's declared data. Coordinates are fractions of the surface. */
async function pixel(surface: Locator, x: number, y: number) {
  return surface.locator("canvas").evaluate(
    (canvas: HTMLCanvasElement, at) => {
      return Array.from(
        canvas
          .getContext("2d")!
          .getImageData(Math.floor(at.x * canvas.width), Math.floor(at.y * canvas.height), 1, 1)
          .data,
      );
    },
    { x, y },
  );
}

async function hasInk(surface: Locator) {
  return surface.locator("canvas").evaluate((canvas: HTMLCanvasElement) => {
    const pixels = canvas.getContext("2d")!.getImageData(0, 0, canvas.width, canvas.height).data;
    return pixels.some((value, index) => index % 4 === 3 && value > 0);
  });
}

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  pageErrors.set(page, errors);
  page.on("pageerror", (error) => errors.push(error.message));
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

test("首页时间范围输入完整可见，不挤压或横向滚动", async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem("heartbeat-theme", "dark"));
  await recordingRoutes(page);
  await page.goto("/");
  for (const width of [1280, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await page.getByRole("button", { name: "时间范围", exact: true }).click();
    const panel = page.getByRole("dialog", { name: "时间范围", exact: true });
    await expect(panel).toBeVisible();
    expect(await panel.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
      true,
    );
    const start = (await panel.getByLabel("开始时间").boundingBox())!;
    const end = (await panel.getByLabel("结束时间").boundingBox())!;
    expect(start.y + start.height).toBeLessThanOrEqual(end.y);
    expect(start.width).toBeGreaterThan(240);
    await page.screenshot({ path: evidencePath(`overview-range-${width}.png`) });
    await page.keyboard.press("Escape");
  }
  const trigger = page.getByRole("button", { name: "时间范围", exact: true });
  const panel = page.getByRole("dialog", { name: "时间范围", exact: true });
  await trigger.click();
  const original = await panel.getByLabel("开始时间").inputValue();
  await panel.getByLabel("开始时间").fill("2026-09-20T12:30");
  await panel.getByRole("button", { name: "取消", exact: true }).click();
  await expect(trigger).toBeFocused();
  await trigger.click();
  await expect(panel.getByLabel("开始时间")).toHaveValue(original);
  await panel.getByLabel("开始时间").fill("2026-09-20T12:30");
  await panel.getByLabel("结束时间").fill("2026-09-28T19:45");
  await panel.getByRole("button", { name: "应用范围" }).click();
  await expect(panel).toHaveCount(0);
  await expect(trigger).toContainText("2026/09/20 — 2026/09/28");
  await trigger.click();
  await expect(panel.getByLabel("开始时间")).toHaveValue("2026-09-20T12:30");
  await expect(panel.getByLabel("结束时间")).toHaveValue("2026-09-28T19:45");
});

test("统一时间线自动读取完整区间并可查看原始记录", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto(desktopPath);
  await page.getByRole("button", { name: "展开导航" }).click();
  await expect(
    page.getByRole("navigation", { name: "主导航" }).getByRole("link", { name: "Hub 管理" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "收起导航" }).press("Escape");
  await expect(page).toHaveTitle(/Heartbeat/);
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toBeVisible();
  const ranges = page.getByRole("button", { name: /当前 com\.apple\.finder/ });
  await expect(page.getByRole("region", { name: "区间记录列表" })).toContainText(
    "com.microsoft.VSCode",
  );
  await ranges.press("ArrowRight");
  await page.getByRole("button", { name: /当前 记录解析失败/ }).press("Enter");
  await page.getByText("记录内容与原始详情", { exact: true }).click();
  const failure = page.getByRole("region", { name: "所选记录详情" }).getByRole("alert");
  await expect(failure).toContainText("记录解析失败");
  await expect(failure).toContainText("malformed-observation");
  await expect(page.getByRole("region", { name: "区间记录列表" })).toContainText(
    "com.microsoft.VSCode",
  );
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
  await page.goto(desktopPath);
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
  await page.goto(desktopPath);
  await expect(page.getByRole("heading", { name: "还没有可以回放的记录" })).toBeVisible();
});

test("请求失败后可重试恢复", async ({ page }) => {
  await recordingRoutes(page, { fail: true });
  await page.goto(desktopPath);
  await expect(page.getByRole("heading", { name: "暂时无法读取采集来源" })).toBeVisible({
    timeout: 15000,
  });
  await recordingRoutes(page);
  await page.getByRole("button", { name: "重试" }).click();
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toBeVisible();
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

  await page.goto(desktopPath);
  const failureAlert = page.locator(".density-error");
  await expect(failureAlert).toContainText("1 条 Track 读取失败");
  await expect(page.getByText("观测状态", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toHaveCount(0);

  failDesktop = false;
  await page.getByRole("button", { name: "重试失败 Track" }).click();
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toBeVisible();
  await expect(failureAlert).toHaveCount(0);
});

test("部分 Track 失败而其余仍在加载时不误报全部失败", async ({ page }) => {
  await recordingRoutes(page);
  let failedRequests = 0;
  let release!: () => void;
  const waiting = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/api/v1/tracks?**", (route) =>
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
    await page.goto(desktopPath);
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
  await page.goto(desktopPath);
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toBeVisible();
  await page.getByRole("button", { name: "展开导航" }).click();
  await page.getByRole("button", { name: "退出登录" }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), userStorageKey)).toBeNull();
  await page.goto(desktopPath);
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toHaveCount(0);
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
      value: {},
      objects: [deviceReference, { ...appReference(name), id: name }],
    }));
    await route.fulfill({ json: { track: desktopTrack, records, nextCursor: null } });
  });
  await page.goto(desktopPath);
  await page.getByRole("button", { name: "全天", exact: true }).click();
  const lane = page.locator(".timeline-range-plot").first();
  const box = (await lane.boundingBox())!;
  const x = box.x + box.width / 4;
  await expect.poll(async () => (await pixel(lane, 0.25, 18 / box.height))[3]).toBeGreaterThan(0);
  await expect.poll(async () => (await pixel(lane, 0.25, 46 / box.height))[3]).toBeGreaterThan(0);
  expect((await pixel(lane, 0.5, 18 / box.height))[3]).toBe(0);
  const light = await pixel(lane, 0.25, 18 / box.height);
  await page.getByRole("button", { name: "展开导航" }).click();
  await page.getByRole("button", { name: "切换到深色" }).click();
  await expect(page.getByRole("button", { name: "展开导航" })).toBeVisible();
  await expect.poll(() => pixel(lane, 0.25, 18 / box.height)).not.toEqual(light);
  // Empty time is neither painted nor selectable.
  await page.mouse.click(box.x + box.width / 2, box.y + 18);
  await expect(page.getByRole("region", { name: "所选记录详情" })).toHaveCount(0);
  // Known fixture spans 04:00–08:00; its overlapping records must occupy separate rows.
  await page.mouse.click(x, box.y + 18);
  await expect(page.getByRole("region", { name: "所选记录详情" })).toContainText("Overlap A");
  await page.mouse.click(x, box.y + 46);
  await expect(page.getByRole("region", { name: "所选记录详情" })).toContainText("Overlap B");
  await lane.focus();
  await lane.press("ArrowRight");
  await expect(lane).toHaveAttribute("aria-label", /当前 Overlap B/);
  await lane.press("Enter");
  await expect(page.getByRole("region", { name: "所选记录详情" })).toContainText("Overlap B");
  await page.setViewportSize({ width: 900, height: 800 });
  await expect.poll(async () => (await pixel(lane, 0.25, 18 / box.height))[3]).toBeGreaterThan(0);
  await page.screenshot({ path: evidencePath("replay-overlapping.png"), fullPage: true });
});

test("概览拖选和手柄调整同步泳道，并按固定时间片查询更细的输入密度", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto(desktopPath);
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
  const day = new Date();
  day.setHours(0, 0, 0, 0);
  const nextDay = new Date(day);
  nextDay.setDate(nextDay.getDate() + 1);
  const range = new URLSearchParams({ from: day.toISOString(), to: nextDay.toISOString() });
  await page.goto(`${desktopPath}?${range}`);
  await page.getByRole("button", { name: "全天", exact: true }).click();
  const curve = page.locator(".timeline-density-curve").first();
  await expect(curve.locator("canvas")).toBeVisible();
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
  await expect(curve.locator("canvas")).toBeVisible();
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
  await expect(curve).toHaveAttribute("data-keyboard-at", /\d+/);
  await page.keyboard.press("Enter");
  await expect(page.locator(".point-details")).toBeVisible();
});

test("应用子泳道与记录详情联动，日期切换清理选中记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto(desktopPath);
  await page.getByRole("button", { name: "展开 测试 Mac 的应用" }).click();
  const app = page
    .locator(".application-sublane")
    .filter({ has: page.getByText("com.apple.finder", { exact: true }) });
  await expect(app).toBeVisible();
  await app.getByRole("button", { name: /当前 com\.apple\.finder/ }).press("Enter");
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
  await page.goto(desktopPath);

  const timeline = page.getByRole("region", { name: "活动泳道，方向键平移，加减键缩放" });
  await expect(timeline.getByText("前台应用", { exact: true })).toBeVisible();
  await expect(timeline.getByText("观测状态", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "区间记录列表" })).toContainText("观测状态");

  const warning = page.getByRole("button", {
    name: /当前 应用观测 · 缺少权限 · screen-recording/,
  });
  await expect(warning).toBeVisible();
  await warning.press("Enter");
  await expect(page.getByRole("region", { name: "所选记录详情" })).toContainText(
    "应用观测 · 缺少权限",
  );
  await page.screenshot({ path: evidencePath("replay-observation-track.png"), fullPage: true });
});

test("泳道滚动、平移缩放与键盘范围控制始终保持在日期边界内", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto(desktopPath);
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
  await page.goto(desktopPath);
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

test("泳道悬停提示是跟随指针的浮层，离开即收起", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto(desktopPath);
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
  const range = page.getByRole("button", { name: /当前 com\.apple\.finder/ }).first();
  const rangeBox = (await range.boundingBox())!;
  const finderAt = Date.now() - 29 * 60_000;
  await page.mouse.move(
    rangeBox.x + ((finderAt - start) / (end - start)) * rangeBox.width,
    rangeBox.y + 18,
  );
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
  await page.goto(desktopPath);
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
  await page.getByRole("button", { name: /当前 com\.apple\.finder/ }).press("Enter");
  const inspecting = await readStart();
  await page.clock.fastForward(30_000);
  expect(await readStart()).toBe(inspecting);
  await expect(page.getByRole("region", { name: "所选记录详情" })).toBeVisible();
  await page.screenshot({ path: evidencePath("replay-follow-paused.png"), fullPage: true });
});

test("历史日期在完整读取后聚焦首条活动，刷新不会重新定位", async ({ page }) => {
  await recordingRoutes(page);
  await page.route("**/api/v1/tracks?**", (route) =>
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
            objects: [deviceReference, appReference("com.apple.finder")],
            value: {},
          },
        ],
        nextCursor: null,
      },
    });
  });
  await page.goto(desktopPath);
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

test("从设备进入应用保留设备和时间条件", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  const link = page.getByLabel("对象摘要").getByRole("link", { name: /测试 Mac/ });
  const href = new URL((await link.getAttribute("href"))!, "http://localhost");
  await link.click();
  await expect(page.getByRole("heading", { name: "测试 Mac", exact: true })).toBeVisible();
  await page
    .getByRole("navigation", { name: "相关对象" })
    .getByRole("link", { name: "com.apple.finder", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: "com.apple.finder", exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.get("context")).toBe(desktopObject.id);
  expect(new URL(page.url()).searchParams.get("from")).toBe(href.searchParams.get("from"));
  await expect
    .poll(() =>
      requests.some(
        (url) =>
          url.searchParams.get("objectId") === appReference("com.apple.finder").id &&
          url.searchParams.get("contextObjectIds") === desktopObject.id,
      ),
    )
    .toBe(true);
  await page.getByRole("link", { name: "全部记录", exact: true }).click();
  await expect.poll(() => new URL(page.url()).searchParams.has("context")).toBe(false);
});

test("VRChat 账号通过对象页切换世界视图", async ({ page }) => {
  const account = {
    id: "019e0000-0000-7000-8000-000000000093",
    namespace: "vrchat.account",
    key: "usr_fixture",
    name: "示例账号",
    roles: ["account"],
  };
  const world = {
    id: "019e0000-0000-7000-8000-000000000094",
    namespace: "vrchat.world",
    key: "wrld_fixture",
    name: "Quiet Aquarium",
    roles: ["world"],
  };
  const friend = {
    ...account,
    id: "019e0000-0000-7000-8000-000000000095",
    key: "usr_friend",
    name: "同行好友",
    roles: ["friend"],
  };
  const queries: URL[] = [];
  const track = { ...desktopTrack, id: "vrc-location", type: "vrchat.location" };
  await page.route("**/api/v1/objects**", (route) => {
    const path = new URL(route.request().url()).pathname;
    return route.fulfill({
      json: [account, world, friend].find((item) => path === `/api/v1/objects/${item.id}`) ?? {
        objects: [account, world, friend],
      },
    });
  });
  await page.route("**/api/v1/tracks**", (route) => {
    const url = new URL(route.request().url());
    queries.push(url);
    const records = [
      {
        id: "visit",
        startedAt: new Date(Date.now() - 3600000).toISOString(),
        endedAt: new Date().toISOString(),
        observedAt: null,
        receivedAt: new Date().toISOString(),
        value: { instance_id: "100", basis: "api_visible" },
        objects: [
          { ...account, role: "account" },
          { ...world, role: "world" },
          { ...friend, role: "friend" },
        ],
      },
    ];
    return route.fulfill({
      json:
        url.pathname === "/api/v1/tracks"
          ? { tracks: [track] }
          : { track, records, nextCursor: null },
    });
  });
  await page.goto("/");
  await page.getByRole("link", { name: "VRChat ↗" }).click();
  await page.getByRole("link", { name: /示例账号/ }).click();
  await page
    .getByRole("navigation", { name: "对象视图" })
    .getByRole("link", { name: "世界与相遇" })
    .click();
  await expect(page.getByRole("heading", { name: "常去的世界" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Quiet Aquarium/ }).first()).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
    .toBe(true);
  await page.getByRole("link", { name: "查看详细时间线" }).click();
  await expect(page.getByRole("region", { name: "区间记录列表" })).toContainText("Quiet Aquarium");
  const related = page.getByRole("navigation", { name: "相关对象" });
  await related.getByRole("link", { name: world.name, exact: true }).click();
  await expect(page.getByRole("heading", { name: world.name, exact: true })).toBeVisible();
  await related.getByRole("link", { name: friend.name, exact: true }).click();
  await expect(page.getByRole("heading", { name: friend.name, exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.getAll("context")).toEqual([account.id, world.id]);
  await expect
    .poll(() =>
      queries.some(
        (url) =>
          url.searchParams.get("objectId") === friend.id &&
          url.searchParams.getAll("contextObjectIds").join(",") ===
            [account.id, world.id].join(","),
      ),
    )
    .toBe(true);
  await page.getByRole("link", { name: "返回上一级", exact: true }).click();
  await expect(page.getByRole("heading", { name: world.name, exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.getAll("context")).toEqual([account.id]);
  await page.getByRole("link", { name: "返回上一级", exact: true }).click();
  await expect(page.getByRole("heading", { name: account.name, exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.getAll("context")).toEqual([]);
});

test("跨日活动概览显示日期，窄屏刻度不重叠", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto(
    `${desktopPath}?from=2026-09-24T16%3A00%3A00.000Z&to=2026-10-01T16%3A00%3A00.000Z`,
  );
  const labels = page.locator(".overview-ticks span");
  await expect(labels.first()).toBeVisible();
  expect(await labels.allTextContents()).not.toContain("次日 00:00");
  for (const label of await labels.allTextContents()) expect(label).toMatch(/^\d+\/\d+$/);
  await page.setViewportSize({ width: 320, height: 800 });
  const boxes = await labels.evaluateAll((items) =>
    items.map((item) => {
      const rect = item.getBoundingClientRect();
      return { left: rect.left, right: rect.right };
    }),
  );
  for (let index = 1; index < boxes.length; index++)
    expect(boxes[index]!.left).toBeGreaterThan(boxes[index - 1]!.right);
  await page.screenshot({ path: evidencePath("replay-week-mobile.png"), fullPage: true });
});

test("概览为不适用桌面时长的对象保留入口", async ({ page }) => {
  await recordingRoutes(page);
  const steps = {
    ...desktopObject,
    id: "steps",
    namespace: "wechat.account",
    roles: ["account"],
    name: "步数账号",
  };
  await page.route("**/api/v1/objects**", (route) =>
    route.fulfill({ json: { objects: [desktopObject, steps] } }),
  );
  await page.route("**/api/v1/tracks?**", (route) =>
    route.fulfill({ json: { tracks: [{ ...desktopTrack, type: "browser.page" }] } }),
  );
  await page.goto("/");
  const sources = page.getByLabel("对象摘要");
  await expect(sources.getByRole("link", { name: /测试 Mac/ })).toBeVisible();
  await expect(sources.getByRole("link", { name: /步数账号/ })).toBeVisible();
  await expect(page.getByRole("button", { name: "刷新", exact: true })).toBeEnabled();
  await expect(sources.getByText("暂无记录")).toHaveCount(0);
  await expect(sources.getByText("0 个应用")).toHaveCount(0);
  await expect(sources.getByRole("img", { name: "每日时长" })).toHaveCount(0);
});
