import { expect, test } from "@playwright/test";
import { identityRoutes, seedSession } from "./fixtures";

test("Hub status separates presence, collection and delivery without remote lifecycle controls", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.emulateMedia({ colorScheme: "dark" });
  await identityRoutes(page);
  await seedSession(page);
  let online = true;
  await page.route("**/api/v1/hubs**", async (route) => {
    if (route.request().url().endsWith("/activity")) {
      await route.fulfill({ json: { activities: {} } });
      return;
    }
    await route.fulfill({
      json: {
        hubs: [
          {
            id: "hub-a",
            lastSeenAt: new Date().toISOString(),
            online,
            retired: false,
            report: {
              displayName: "测试服务器",
              kind: "server",
              types: [{ key: "example", displayName: "示例采集", fields: [] }],
              collectors: [
                {
                  key: "example",
                  target: "account",
                  displayName: "账号采集",
                  state: "running",
                  error: null,
                },
              ],
              delivery: { pending: 7, failed: 1, error: null },
            },
          },
        ],
      },
    });
  });
  await page.goto("/hubs");
  const hub = page.getByRole("region", { name: "测试服务器", exact: true });
  await expect(hub.getByText("hub-a", { exact: true })).toBeHidden();
  await hub.getByText("查看标识", { exact: true }).click();
  await expect(hub.getByText("hub-a", { exact: true })).toBeVisible();
  await hub.getByText("查看标识", { exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath("hubs-desktop-dark.png"), fullPage: true });
  await expect(page.getByText("待上传 7")).toBeVisible();
  await expect(page.getByText("失败 1", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "登录 示例采集", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "重新登录", exact: true })).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "添加其他 示例采集 账号", exact: true }),
  ).toBeEnabled();
  await expect(page.getByRole("button", { name: /^(开始|暂停|配置|移除|退役 Hub)$/ })).toHaveCount(
    0,
  );
  online = false;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await expect(page.getByText("离线", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("button", { name: "添加其他 示例采集 账号", exact: true }),
  ).toBeDisabled();
  await expect(hub.getByText("最近上报：采集中", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "切换到浅色" }).click();
  await page.screenshot({ path: testInfo.outputPath("hubs-desktop-light.png"), fullPage: true });
});

test("Collector login discovers identity and completes two-factor authentication without manual start", async ({
  page,
}, testInfo) => {
  await identityRoutes(page);
  await seedSession(page);
  const fields = [
    { name: "username", label: "用户名", kind: "text", required: true },
    { name: "password", label: "密码", kind: "secret", required: true },
  ];
  const requests: {
    key: string;
    target?: string;
    sessionId?: string;
    input: Record<string, unknown>;
  }[] = [];
  let authenticated = false;
  let expired = false;
  await page.route("**/api/v1/hubs**", async (route) => {
    if (route.request().url().endsWith("/activity")) {
      await route.fulfill({ json: { activities: {} } });
      return;
    }
    if (route.request().method() === "POST") {
      expect(route.request().url()).toMatch(/\/login$/);
      requests.push(route.request().postDataJSON());
      authenticated = requests.length >= 3;
      expired = false;
      await route.fulfill({
        json: {
          id: "operation",
          succeeded: true,
          login: authenticated
            ? { target: "discovered-account", fields: [], sessionId: null, error: null }
            : {
                target: null,
                sessionId: "pending-login",
                fields: [{ name: "code", label: "验证码", kind: "secret", required: true }],
                error: requests.length === 2 ? "验证码错误" : null,
              },
        },
      });
      return;
    }
    await route.fulfill({
      json: {
        hubs: [
          {
            id: "hub",
            lastSeenAt: new Date().toISOString(),
            online: true,
            retired: false,
            report: {
              displayName: "登录测试",
              kind: "server",
              collectors: authenticated
                ? [
                    {
                      key: "custom",
                      target: "discovered-account",
                      displayName: "已登录账号",
                      state: expired ? "authentication_required" : "running",
                      error: null,
                    },
                  ]
                : [],
              delivery: { pending: 0, failed: 0, error: null },
              types: [{ key: "custom", displayName: "自定义采集", fields }],
            },
          },
        ],
      },
    });
  });
  await page.goto("/hubs");
  await page.getByRole("button", { name: "登录 自定义采集" }).click();
  await expect(page.getByLabel("账号 ID")).toHaveCount(0);
  await page.getByLabel("用户名", { exact: true }).fill("user");
  await page.getByLabel("密码", { exact: true }).fill("private-password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByLabel("密码", { exact: true })).toHaveCount(0);
  await page.getByRole("form", { name: "自定义采集 登录" }).screenshot({
    path: testInfo.outputPath("collector-two-factor.png"),
  });
  await page.getByLabel("验证码", { exact: true }).fill("wrong-code");
  await page.getByRole("button", { name: "继续登录", exact: true }).click();
  await expect(page.getByText("验证码错误", { exact: true })).toBeVisible();
  await expect(page.getByLabel("验证码", { exact: true })).toHaveValue("");
  await page.getByLabel("验证码", { exact: true }).fill("123456");
  await page.getByRole("button", { name: "继续登录", exact: true }).click();
  await expect(page.getByText("采集中", { exact: true })).toBeVisible();
  await expect(page.getByRole("form", { name: "自定义采集 登录" })).toHaveCount(0);
  expect(requests).toEqual([
    { key: "custom", input: { username: "user", password: "private-password" } },
    { key: "custom", sessionId: "pending-login", input: { code: "wrong-code" } },
    { key: "custom", sessionId: "pending-login", input: { code: "123456" } },
  ]);
  await expect(page.getByRole("button", { name: "登录 自定义采集", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "添加其他 自定义采集 账号", exact: true }).click();
  await expect(page.getByLabel("用户名", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "关闭", exact: true }).click();
  expired = true;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByRole("button", { name: "重新登录", exact: true }).click();
  await page.getByLabel("用户名", { exact: true }).fill("user");
  await page.getByLabel("密码", { exact: true }).fill("replacement-password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByText("采集中", { exact: true })).toBeVisible();
  await expect(page.getByRole("form", { name: "自定义采集 登录" })).toHaveCount(0);
  expect(requests[3]).toEqual({
    key: "custom",
    target: "discovered-account",
    input: { username: "user", password: "replacement-password" },
  });
  await page.getByRole("region", { name: "登录测试", exact: true }).screenshot({
    path: testInfo.outputPath("collector-resumed.png"),
  });
});

test("Hub curves show counter deltas without replaying startup, outages or restarts", async ({
  page,
}, testInfo) => {
  await identityRoutes(page);
  await seedSession(page);
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const activity = { epoch: "run-a", capturedAt: Date.now(), accepted: 1000, delivered: 800 };
  let available = true;
  await page.route("**/api/v1/hubs**", async (route) => {
    if (route.request().url().endsWith("/activity")) {
      await route.fulfill({ json: { activities: available ? { hub: activity } : {} } });
      return;
    }
    await route.fulfill({
      json: {
        hubs: [
          {
            id: "hub",
            online: true,
            retired: false,
            lastSeenAt: new Date().toISOString(),
            report: {
              displayName: "收发验收",
              kind: "desktop",
              types: [],
              collectors: [],
              delivery: { pending: 20, failed: 0, error: null },
            },
          },
        ],
      },
    });
  });
  await page.goto("/hubs");
  const flow = page.getByLabel("Hub 最近收发活动");
  const plot = flow.getByRole("img");
  const summary = flow.getByLabel("本次收发数量");
  const tooltip = flow.getByRole("tooltip");
  await expect(flow.getByText("最近 60 秒 · Records")).toBeVisible();
  await expect(plot.locator("canvas")).toBeVisible();
  await expect(summary).toHaveText("等待下一次更新");
  await expect(plot).toHaveAttribute("data-samples", "1");
  const start = await plot.getAttribute("data-range");
  await expect.poll(() => plot.getAttribute("data-range")).not.toBe(start);

  async function advance(accepted: number, delivered: number) {
    activity.capturedAt += 1000;
    activity.accepted += accepted;
    activity.delivered += delivered;
    await expect(summary).toHaveText(`已接受 ${accepted}，已上传 ${delivered}`);
  }
  await advance(100, 0);
  await advance(50, 80);
  // Counter/tooltip assertions alone miss a curve rendered with an invalid CSS color.
  await expect
    .poll(() =>
      plot.locator("canvas").evaluate((canvas: HTMLCanvasElement) => {
        const context = canvas.getContext("2d")!;
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
        const swatch = document.createElement("canvas").getContext("2d")!;
        return ["--chart-1", "--chart-2"].map((token) => {
          swatch.clearRect(0, 0, 1, 1);
          swatch.fillStyle = getComputedStyle(canvas).getPropertyValue(token).trim();
          swatch.fillRect(0, 0, 1, 1);
          const color = swatch.getImageData(0, 0, 1, 1).data;
          for (let index = 0; index < pixels.length; index += 4) {
            if (
              pixels[index + 3]! > 200 &&
              [0, 1, 2].every((channel) => Math.abs(pixels[index + channel]! - color[channel]!) < 8)
            )
              return true;
          }
          return false;
        });
      }),
    )
    .toEqual([true, true]);
  const bounds = (await plot.locator(".u-over").boundingBox())!;
  const [from, to] = (await plot.getAttribute("data-range"))!.split("/").map(Number);
  const x = Math.min(
    bounds.width - 1,
    ((activity.capturedAt / 1000 - from!) / (to! - from!)) * bounds.width,
  );
  await page.mouse.move(bounds.x + x, bounds.y + bounds.height / 2);
  await expect(tooltip.getByRole("row", { name: "已接受 50" })).toBeVisible();
  await expect(tooltip.getByRole("row", { name: "已上传 80" })).toBeVisible();
  await flow.screenshot({ path: testInfo.outputPath("hub-delivery-deltas.png") });
  await page.mouse.move(0, 0);
  await expect(tooltip).toBeHidden();

  // A duplicate API snapshot must not create another zero-valued sample.
  await page.waitForResponse((response) => response.url().endsWith("/api/v1/hubs/activity"));
  await expect(plot).toHaveAttribute("data-samples", "3");
  await advance(0, 0); // No successful receipt, no delivery count.
  available = false;
  await expect(flow.getByText("活动状态暂不可用")).toBeVisible();
  await expect(summary).toHaveText("暂无数据");
  activity.capturedAt += 6000;
  activity.accepted += 10000;
  activity.delivered += 10000;
  available = true;
  await expect(summary).toHaveText("等待下一次更新"); // Do not turn outage traffic into a spike.
  await advance(7, 3);
  await page.emulateMedia({ reducedMotion: "reduce" });
  activity.epoch = "run-b";
  activity.capturedAt += 1000;
  activity.accepted = 1;
  activity.delivered = 0;
  await expect(summary).toHaveText("等待下一次更新");
  await expect(plot).toHaveAttribute("data-samples", "1");
  await advance(2, 1);
  await page.setViewportSize({ width: 320, height: 800 });
  await page.screenshot({ path: testInfo.outputPath("hub-delivery-mobile.png"), fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
  await page.reload();
  await expect(summary).toHaveText("等待下一次更新");
  await expect(plot).toHaveAttribute("data-samples", "1");
  expect(errors).toEqual([]);
});
