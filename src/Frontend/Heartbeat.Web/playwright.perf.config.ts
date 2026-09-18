import { defineConfig, devices } from "@playwright/test";

const port = process.env.HEARTBEAT_PLAYWRIGHT_PORT ?? "3108";
const baseURL = `http://127.0.0.1:${port}`;
const production = process.env.HEARTBEAT_PERF_MODE === "production";

/**
 * The drag benchmark runs against both servers: `next dev` shows what a developer
 * feels, and the production build shows what an Owner would get.
 */
export default defineConfig({
  testDir: "./tests/perf",
  testMatch: /.*\.perf\.ts$/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 240_000,
  reporter: process.env.HEARTBEAT_PLAYWRIGHT_JSON
    ? [["list"], ["json", { outputFile: process.env.HEARTBEAT_PLAYWRIGHT_JSON }]]
    : "list",
  outputDir: process.env.HEARTBEAT_EVIDENCE_DIR ?? "test-results/perf",
  use: {
    baseURL,
    viewport: { width: 1440, height: 900 },
    trace: "off",
    screenshot: "off",
    video: "off",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: production
      ? "npm run build && node -e \"require('node:fs').cpSync('.next/static', '.next/standalone/.next/static', { recursive: true })\" && node .next/standalone/server.js"
      : `npm run dev -- --hostname 127.0.0.1 --port ${port}`,
    url: `${baseURL}/login`,
    reuseExistingServer: false,
    timeout: 600_000,
    env: {
      HOSTNAME: "127.0.0.1",
      PORT: port,
      NEXT_PUBLIC_OIDC_AUTHORITY: "https://identity.heartbeat.test",
      NEXT_PUBLIC_OIDC_CLIENT_ID: "heartbeat-web-test",
      NEXT_PUBLIC_OIDC_SCOPE: "openid profile",
      HEARTBEAT_API_URL: "http://127.0.0.1:1",
    },
  },
});
