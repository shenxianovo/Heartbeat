import { chromium } from "@playwright/test";
import { verifyReplay, type ReplayWitness } from "./replay-record.ts";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const directory = process.argv[2]!;
const port = Number(process.argv[3]);
if (!directory || !Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error("Usage: desktop-replay.ts <evidence-directory> <isolated-web-port>");
}
const witness: ReplayWitness = JSON.parse(
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
// Keep only API paths and status codes; never authentication URLs, headers or payloads.
const apiResponses: { path: string; status: number }[] = [];
page.on("response", (response) => {
  const pathname = new URL(response.url()).pathname;
  if (!pathname.startsWith("/api/v1/")) return;
  apiResponses.push({ path: pathname, status: response.status() });
  if (apiResponses.length > 30) apiResponses.shift();
});

try {
  await progress("oidc-login");
  await page.goto("/login");
  await page.getByRole("button", { name: "使用 Heartbeat 账号登录" }).click();
  // The human signs in to the real provider. No tokens/sessionStorage are injected or exported.
  await page.getByRole("button", { name: "刷新", exact: true }).waitFor({ timeout: 300_000 });
  await progress("record-query");
  const details = await verifyReplay(page, witness, progress);
  // This scenario observes the controlled Heartbeat application, not arbitrary native content.
  await details
    .locator(".record-time")
    .screenshot({ path: path.join(directory, "replay-record.png") });
  await progress("completed", true);
  console.log(
    "Verified the native application's database Record in the real Web timeline and details.",
  );
} catch (error) {
  // Playwright errors can include login URLs or native page text. Retain stage/type only.
  await writeFile(
    path.join(directory, "replay.json"),
    JSON.stringify(
      { stage, passed: false, error: error instanceof Error ? error.name : "Error", apiResponses },
      null,
      2,
    ),
  );
  console.error(`Desktop replay failed at ${stage}; see replay.json.`);
  process.exitCode = 1;
} finally {
  await context.close();
  await browser.close();
}
