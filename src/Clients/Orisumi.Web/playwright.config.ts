import { defineConfig, devices, type PlaywrightTestProject } from "@playwright/test";

// 本地环境的边缘验证（Web 入口，默认 8081）只在显式 ASPIRE_EDGE=1 时加入，
// 默认 test:e2e 只跑自带 dev server 的用例，不依赖后端。
const projects: PlaywrightTestProject[] = [
  {
    name: "chromium",
    use: { ...devices["Desktop Chrome"] },
  },
];

if (process.env.ASPIRE_EDGE === "1") {
  projects.push({
    name: "edge",
    use: { ...devices["Desktop Chrome"], baseURL: process.env.WEB_URL ?? "http://localhost:8081" },
    testDir: "./tests/aspire",
    testMatch: /edge\.spec\.ts/,
  });
}

export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? "github" : "list",
  use: {
    baseURL: "http://localhost:5173",
    trace: "on-first-retry",
  },
  projects,
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
