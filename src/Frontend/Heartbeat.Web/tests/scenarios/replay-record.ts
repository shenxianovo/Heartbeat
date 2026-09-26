import { expect, type Page } from "@playwright/test";

export type ReplayStage =
  | "browser-start"
  | "oidc-login"
  | "api-reconciliation"
  | "track-query"
  | "range-input"
  | "record-response"
  | "timeline-selection"
  | "hub-activity"
  | "completed";

export interface ReplayWitness {
  record: { trackId: string; recordId: string; startedAt: string; endedAt: string };
  target: string;
  applicationId: string;
  applicationName: string;
  applicationKind: string;
}

export async function verifyReplay(
  page: Page,
  expected: ReplayWitness,
  progress: (stage: ReplayStage) => Promise<void>,
) {
  await progress("track-query");
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

  await progress("range-input");
  const from = new Date(Date.parse(expected.record.startedAt) - 60_000).toISOString().slice(0, 16);
  const to = new Date(Date.parse(expected.record.endedAt) + 120_000).toISOString().slice(0, 16);
  await page.getByRole("button", { name: "自定义时间范围" }).click();
  const range = page.getByRole("dialog", { name: "自定义时间范围" });
  await range.getByLabel("开始时间").fill(from);
  await range.getByLabel("结束时间").fill(to);
  await progress("record-response");
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
    application: {
      platform: "macos",
      id_kind: expected.applicationKind,
      id: expected.applicationId,
    },
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
  return details;
}

async function selectRecord(page: Page, expected: ReplayWitness) {
  const timeline = page.getByRole("region", { name: "活动泳道，方向键平移，加减键缩放" });
  const lanes = timeline.getByRole("button", { name: /条区间；方向键浏览，回车查看/ });
  await expect(lanes.first()).toBeVisible();
  // Each Canvas lane is one keyboard target. Traverse records as a keyboard user does;
  // application names can repeat across gaps, so the selected Record ID is the witness.
  for (const lane of await lanes.all()) {
    const count = Number(await lane.getAttribute("data-segments"));
    for (let index = 0; index < count; index++) {
      await lane.press("Enter");
      const details = page.getByRole("region", { name: "所选记录详情" });
      const expand = details.getByRole("button", { name: "查看详情", exact: true });
      if (await expand.isVisible()) await expand.click();
      if (await details.getByText(expected.record.recordId, { exact: true }).isVisible()) return;
      await lane.press("ArrowRight");
    }
  }
  throw new Error("The database Record could not be selected in the timeline.");
}
