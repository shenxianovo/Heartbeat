import { chromium, expect, type APIRequestContext } from "@playwright/test";
import { writeFile } from "node:fs/promises";
import { verifyReplay, type ReplayWitness, type ReplayStage } from "./replay-record.ts";

interface RecordSnapshot {
  id: string;
  startedAt: string | null;
  endedAt: string | null;
  observedAt: string | null;
  value: unknown;
}
interface DeliveredRecord {
  trackId: string;
  record: RecordSnapshot;
}
const input = JSON.parse(process.env.HEARTBEAT_REPLAY_INPUT!) as {
  web: string;
  session: {
    authority: string;
    clientId: string;
    token: { value: string; ownerId: string; expiresAt: string };
  } | null;
  witness: ReplayWitness;
  files: { report: string; screenshot: string };
  records: DeliveredRecord[];
};
delete process.env.HEARTBEAT_REPLAY_INPUT;
const witness = input.witness;
let stage: ReplayStage = "browser-start";
const apiResponses: { path: string; status: number }[] = [];
async function progress(next: ReplayStage, passed = false) {
  stage = next;
  await writeFile(
    input.files.report,
    JSON.stringify(
      {
        stage,
        passed,
        recordId: witness.record.recordId,
        verifiedRecords: interactive ? null : input.records.length,
      },
      null,
      2,
    ),
  );
}

async function verifyApi(request: APIRequestContext) {
  if (!input.session) return;
  const times = input.records
    .flatMap(({ record }) => [record.startedAt, record.endedAt, record.observedAt])
    .filter((value): value is string => value !== null)
    .map(Date.parse);
  const from = new Date(Math.min(...times) - 1000).toISOString();
  const to = new Date(Math.max(...times) + 1000).toISOString();
  for (const trackId of new Set(input.records.map((item) => item.trackId))) {
    const response = await request.get(`${input.web}api/v1/tracks/${trackId}/records`, {
      params: { from, to, limit: 500 },
      headers: { Authorization: `Bearer ${input.session.token.value}` },
    });
    expect(response.ok()).toBe(true);
    const body = await response.json();
    expect(body.nextCursor).toBeNull();
    const actual: RecordSnapshot[] = body.records;
    const expected = input.records.filter((item) => item.trackId === trackId);
    expect(actual.length).toBe(expected.length);
    for (const { record } of expected) {
      const found = actual.find((item) => item.id === record.id);
      expect(found).toBeDefined();
      expect(found!.value).toEqual(record.value);
      for (const field of ["startedAt", "endedAt", "observedAt"] as const) {
        expect(found![field] === null ? null : Date.parse(found![field]!)).toBe(
          record[field] === null ? null : Date.parse(record[field]!),
        );
      }
    }
  }
}

const interactive = input.session === null;
const browser = await chromium.launch({
  headless: !interactive,
  // Keep the registered OIDC origin, routing sockets to this run's isolated Web.
  args: interactive ? [`--host-rules=MAP localhost:3000 127.0.0.1:${new URL(input.web).port}`] : [],
});
try {
  const context = await browser.newContext({
    baseURL: interactive ? "http://localhost:3000" : input.web,
    timezoneId: "UTC",
    viewport: { width: 1440, height: 1000 },
  });
  if (input.session)
    await context.addInitScript(
      ({ web, session }) => {
        if (location.origin !== new URL(web).origin) return;
        sessionStorage.setItem(
          `oidc.user:${session.authority}:${session.clientId}`,
          JSON.stringify({
            access_token: session.token.value,
            token_type: "Bearer",
            scope: "openid profile",
            expires_at: Math.floor(Date.parse(session.token.expiresAt) / 1000),
            profile: { sub: session.token.ownerId },
          }),
        );
      },
      { web: input.web, session: input.session },
    );
  const page = await context.newPage();
  page.setDefaultTimeout(30_000);
  page.on("response", (response) => {
    const pathname = new URL(response.url()).pathname;
    if (!pathname.startsWith("/api/v1/")) return;
    apiResponses.push({ path: pathname, status: response.status() });
    if (apiResponses.length > 30) apiResponses.shift();
  });
  if (interactive) {
    await progress("oidc-login");
    await page.goto("/login");
    await page.getByRole("button", { name: "使用 Heartbeat 账号登录" }).click();
    await page.getByRole("button", { name: "刷新", exact: true }).waitFor({ timeout: 300_000 });
  } else {
    await progress("api-reconciliation");
    await verifyApi(context.request);
    await page.goto("/");
  }
  const details = await verifyReplay(page, witness, progress);
  // Retain timestamps only: the surrounding details can contain native user context.
  await details.locator(".record-time").screenshot({ path: input.files.screenshot });
  await progress("completed", true);
} catch (error) {
  // Assertion messages and page text can contain tokens or native values. Never export them.
  await writeFile(
    input.files.report,
    JSON.stringify(
      {
        stage,
        passed: false,
        error: error instanceof Error ? error.name : "Error",
        apiResponses,
      },
      null,
      2,
    ),
  );
  console.error(`Desktop replay failed at ${stage}; see replay.json.`);
  process.exitCode = 1;
} finally {
  await browser.close();
}
