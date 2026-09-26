import { expect, test } from "@playwright/test";
import { identityRoutes, seedSession } from "./fixtures";

test("Hub management separates presence, collection and delivery and applies online operations", async ({
  page,
}) => {
  await identityRoutes(page);
  await seedSession(page);
  const collector = {
    key: "example",
    target: "account",
    displayName: "账号采集",
    state: "running",
    error: null,
    configuration: {},
  };
  const report = {
    displayName: "测试服务器",
    kind: "server",
    types: [
      { key: "example", displayName: "示例采集", fields: [], targetLabel: "账号 ID", canAdd: true },
    ],
    collectors: [collector],
    delivery: { pending: 7, failed: 1, error: null },
  };
  let online = true;
  const operations: unknown[] = [];
  await page.route("**/api/v1/hubs**", async (route) => {
    if (route.request().url().endsWith("/activity")) {
      await route.fulfill({ json: { activities: {} } });
      return;
    }
    if (route.request().method() === "GET") {
      await route.fulfill({
        json: {
          hubs: [
            { id: "hub-a", lastSeenAt: new Date().toISOString(), online, retired: false, report },
          ],
        },
      });
    } else {
      operations.push(route.request().postDataJSON());
      collector.state = "paused";
      await route.fulfill({ json: { id: "operation-a", succeeded: true, error: null } });
    }
  });
  await page.goto("/hubs");
  await expect(page.getByRole("heading", { name: "我的 Hub" })).toBeVisible();
  await expect(page.getByText("待上传 7")).toBeVisible();
  await expect(page.getByText("失败 1", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "暂停", exact: true }).click();
  await expect(page.getByText("已暂停", { exact: true })).toBeVisible();
  expect(operations).toEqual([{ action: "pause", key: "example", target: "account" }]);
  online = false;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await expect(page.getByText("离线", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "开始", exact: true })).toBeDisabled();
  await expect(page.getByText("下方为最近上报的信息，当前无法执行采集操作。")).toBeVisible();
});

test("Installed Collector fields drive configuration without persisting secret values in the form", async ({
  page,
}) => {
  await identityRoutes(page);
  await seedSession(page);
  const operations: { configuration: Record<string, unknown> }[] = [];
  await page.route("**/api/v1/hubs**", async (route) => {
    if (route.request().url().endsWith("/activity")) {
      await route.fulfill({ json: { activities: {} } });
      return;
    }
    if (route.request().method() === "POST") {
      operations.push(route.request().postDataJSON());
      await route.fulfill({ json: { id: "operation", succeeded: true } });
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
              displayName: "配置测试",
              kind: "server",
              collectors: [],
              delivery: { pending: 0, failed: 0, error: null },
              types: [
                {
                  key: "custom",
                  displayName: "自定义采集",
                  canAdd: true,
                  targetLabel: "账号 ID",
                  fields: [{ name: "password", label: "密码", kind: "secret", required: false }],
                },
              ],
            },
          },
        ],
      },
    });
  });
  await page.goto("/hubs");
  await page.getByRole("button", { name: "添加 自定义采集" }).click();
  await page.getByLabel("账号 ID").fill("account-a");
  await page.getByLabel("密码", { exact: true }).fill("private-password");
  await page.getByRole("button", { name: "保存配置" }).click();
  await expect(page.getByLabel("密码", { exact: true })).toHaveValue("");
  expect(operations[0]?.configuration.password).toBe("private-password");
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
  const bounds = (await plot.locator(".u-over").boundingBox())!;
  const [from, to] = (await plot.getAttribute("data-range"))!.split("/").map(Number);
  const x = Math.min(
    bounds.width - 1,
    ((activity.capturedAt / 1000 - from!) / (to! - from!)) * bounds.width,
  );
  await page.mouse.move(bounds.x + x, bounds.y + 80);
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
