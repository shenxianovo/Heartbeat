import { chromium, expect, type APIRequestContext } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { verifyReplay, type ReplayWitness } from "./replay-record.ts";

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
  };
  records: DeliveredRecord[];
};
delete process.env.HEARTBEAT_REPLAY_INPUT;
const directory = process.argv[2]!;
const witness: ReplayWitness = JSON.parse(
  await readFile(path.join(directory, "replay-expectation.json"), "utf8"),
);
let stage = "browser-start";
const apiResponses: { path: string; status: number }[] = [];
async function progress(next: string, passed = false) {
  stage = next;
  await writeFile(
    path.join(directory, "replay.json"),
    JSON.stringify(
      {
        stage,
        passed,
        recordId: witness.record.recordId,
        verifiedRecords: input.records.length,
      },
      null,
      2,
    ),
  );
}

async function verifyApi(request: APIRequestContext) {
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

const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({
    baseURL: input.web,
    timezoneId: "UTC",
    viewport: { width: 1440, height: 1000 },
  });
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
  await progress("api-reconciliation");
  await verifyApi(context.request);
  await page.goto("/");
  const details = await verifyReplay(page, witness, progress);
  // Retain timestamps only: the surrounding details can contain native user context.
  await details
    .locator(".record-time")
    .screenshot({ path: path.join(directory, "replay-record.png") });
  await progress("completed", true);
} catch (error) {
  // Assertion messages and page text can contain tokens or native values. Never export them.
  await writeFile(
    path.join(directory, "replay.json"),
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
