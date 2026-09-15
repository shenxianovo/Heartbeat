import { defineConfig, devices } from "@playwright/test";

const port = process.env.HEARTBEAT_PLAYWRIGHT_PORT ?? "3107";
const baseURL = `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.HEARTBEAT_PLAYWRIGHT_JSON
    ? [["list"], ["json", { outputFile: process.env.HEARTBEAT_PLAYWRIGHT_JSON }]]
    : "list",
  outputDir: process.env.HEARTBEAT_EVIDENCE_DIR ?? "test-results",
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `npm run dev -- --hostname 127.0.0.1 --port ${port}`,
    url: `${baseURL}/login`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: {
      NEXT_PUBLIC_OIDC_AUTHORITY: "https://identity.heartbeat.test",
      NEXT_PUBLIC_OIDC_CLIENT_ID: "heartbeat-web-test",
      NEXT_PUBLIC_OIDC_SCOPE: "openid profile",
      HEARTBEAT_API_URL: "http://127.0.0.1:1",
    },
  },
});
