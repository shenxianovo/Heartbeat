import { chromium, expect, type Page } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

interface Witness {
  record: { trackId: string; recordId: string; startedAt: string; endedAt: string };
  target: string;
  applicationId: string;
  applicationName: string;
}

const directory = process.argv[2]!;
const port = Number(process.argv[3]);
if (!directory || !Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error("Usage: collector-replay.ts <evidence-directory> <isolated-web-port>");
}
const witness: Witness = JSON.parse(
  await readFile(path.join(directory, "replay-expectation.json"), "utf8"),
);
let stage = "browser-start";
async function progress(next: string, passed = false) {
  stage = next;
  await writeFile(
    path.join(directory, "replay.json"),
    JSON.stringify({ stage, passed, recordId: witness.record.recordId }, null, 2),
  );
}

// Keep the registered OIDC origin without binding to or touching the developer's port 3000.
// This is Chromium's socket routing, not a Playwright route or a response fixture.
const browser = await chromium.launch({
  headless: false,
  args: [`--host-rules=MAP localhost:3000 127.0.0.1:${port}`],
});
const context = await browser.newContext({
  baseURL: "http://localhost:3000",
  viewport: { width: 1440, height: 1000 },
  timezoneId: "UTC",
});
const page = await context.newPage();
page.setDefaultTimeout(30_000);

try {
  await progress("oidc-login");
  await page.goto("/login");
  await page.getByRole("button", { name: "使用 Heartbeat 账号登录" }).click();
  // The human signs in to the real provider. No tokens/sessionStorage are injected or exported.
  await page.getByRole("heading", { name: "当天经历", exact: true }).waitFor({ timeout: 300_000 });
  await progress("record-query");
  await verifyReplay(page, witness);
  await progress("completed", true);
  console.log(
    "Verified the native application's database Record in the real Web timeline and details.",
  );
} catch (error) {
  // Playwright errors can include login URLs or native page text. Retain stage/type only.
  await writeFile(
    path.join(directory, "replay.json"),
    JSON.stringify(
      { stage, passed: false, error: error instanceof Error ? error.name : "Error" },
      null,
      2,
    ),
  );
  console.error(`Collector replay failed at ${stage}; see replay.json.`);
  process.exitCode = 1;
} finally {
  await context.close();
  await browser.close();
}

async function verifyReplay(page: Page, expected: Witness) {
  const tracksResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v1/tracks" && response.ok(),
  );
  // Reload after login so the authenticated track request is observed from its beginning.
  await page.reload();
  const { tracks } = await (await tracksResponse).json();
  expect(tracks).toEqual(
    expect.arrayContaining([
      expect.objectContaining({
        id: expected.record.trackId,
        collectorKey: "heartbeat.collector.desktop.macos",
        collectorTarget: expected.target,
        type: "desktop.application.foreground",
      }),
    ]),
  );

  const from = new Date(Date.parse(expected.record.startedAt) - 60_000).toISOString().slice(0, 16);
  const to = new Date(Date.parse(expected.record.endedAt) + 120_000).toISOString().slice(0, 16);
  await page.getByRole("button", { name: "自定义时间范围" }).click();
  const range = page.getByRole("dialog", { name: "自定义时间范围" });
  await range.getByLabel("开始时间").fill(from);
  await range.getByLabel("结束时间").fill(to);
  const recordsResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      url.pathname === `/api/v1/tracks/${expected.record.trackId}/records` &&
      url.searchParams.get("from") === `${from}:00.000Z` &&
      response.ok()
    );
  });
  await range.getByRole("button", { name: "应用范围" }).click();
  const { records } = await (await recordsResponse).json();
  const record = records.find((item: { id: string }) => item.id === expected.record.recordId);
  expect(record).toBeDefined();
  expect(Date.parse(record.startedAt)).toBe(Date.parse(expected.record.startedAt));
  expect(Date.parse(record.endedAt)).toBe(Date.parse(expected.record.endedAt));
  expect(record.value).toMatchObject({
    device_id: expected.target,
    application: { platform: "macos", id_kind: "bundle_id", id: expected.applicationId },
  });

  await progress("timeline-selection");
  await selectRecord(page, expected);
  const details = page.getByRole("region", { name: "所选记录详情" });
  await expect(details).toContainText(expected.applicationId);
  await expect(details).toContainText(expected.target);
  await expect(details.getByText(expected.record.recordId, { exact: true })).toBeVisible();
  const formatter = new Intl.DateTimeFormat("zh-CN", {
    timeZone: "UTC",
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
  await expect(details.locator(".record-time")).toContainText(
    formatter.format(new Date(expected.record.startedAt)),
  );
  await expect(details.locator(".record-time")).toContainText(
    formatter.format(new Date(expected.record.endedAt)),
  );
  // Crop out account details and all unrelated native Records.
  await details.screenshot({ path: path.join(directory, "replay-record.png") });
}

async function selectRecord(page: Page, expected: Witness) {
  const timeline = page.getByRole("region", { name: "活动泳道，方向键平移，加减键缩放" });
  const candidates = timeline.getByRole("button", { name: expected.applicationName, exact: true });
  await expect(candidates.first()).toBeVisible();
  // A real observation gap may leave several intervals for the same application.
  // Open the actual witness rather than assuming the first similarly named bar is it.
  for (const candidate of await candidates.all()) {
    await candidate.click();
    await expect(candidate).toHaveAttribute("aria-pressed", "true");
    const details = page.getByRole("region", { name: "所选记录详情" });
    await details.getByRole("button", { name: "查看详情", exact: true }).click();
    if (await details.getByText(expected.record.recordId, { exact: true }).isVisible()) return;
  }
  throw new Error("The database Record could not be selected in the timeline.");
}
