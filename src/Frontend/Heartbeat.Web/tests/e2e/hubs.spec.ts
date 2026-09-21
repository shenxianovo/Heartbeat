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
