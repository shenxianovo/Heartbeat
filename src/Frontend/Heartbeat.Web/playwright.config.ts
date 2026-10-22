import { defineConfig, devices, type PlaywrightTestProject } from "@playwright/test";

// Aspire 边缘验证（8081 入口）只在显式 ASPIRE_EDGE=1 时加入，
// 默认 test:e2e 始终只跑自带 dev server 的用例，不依赖 Aspire 环境。
const projects: PlaywrightTestProject[] = [
  {
    name: "chromium",
    use: { ...devices["Desktop Chrome"] },
  },
];

if (process.env.ASPIRE_EDGE === "1") {
  projects.push({
    name: "edge",
    use: { ...devices["Desktop Chrome"], baseURL: "http://localhost:18081" },
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
