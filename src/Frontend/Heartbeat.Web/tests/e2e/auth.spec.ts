import { expect, test } from "@playwright/test";

import {
  accessToken,
  authority,
  clientId,
  identityRoutes,
  owner,
  recordingRoutes,
} from "./fixtures";

test("登录使用 PKCE，并通过回调恢复会话和回放", async ({ page, baseURL }) => {
  await identityRoutes(page);
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await page.getByRole("button", { name: "使用 Heartbeat 账号登录" }).click();
  await expect(page).toHaveURL(new RegExp(`${authority}/authorize`));
  const request = new URL(page.url());
  expect(request.searchParams.get("response_type")).toBe("code");
  expect(request.searchParams.get("code_challenge_method")).toBe("S256");
  expect(request.searchParams.get("code_challenge")).toMatch(/^[A-Za-z0-9_-]{43,}$/);
  expect(request.searchParams.get("client_id")).toBe(clientId);
  const state = request.searchParams.get("state");
  expect(state).toBeTruthy();

  const now = Math.floor(Date.now() / 1000);
  const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
  const idToken = `${encode({ alg: "RS256", typ: "JWT" })}.${encode({
    iss: authority,
    aud: clientId,
    sub: owner,
    name: "测试用户",
    iat: now,
    exp: now + 3600,
    ...(request.searchParams.get("nonce") ? { nonce: request.searchParams.get("nonce") } : {}),
  })}.test-signature`;
  await page.route(`${authority}/token`, async (route) => {
    const body = new URLSearchParams(route.request().postData() ?? "");
    expect(body.get("grant_type")).toBe("authorization_code");
    expect(body.get("code_verifier")).toMatch(/^[A-Za-z0-9._~-]{43,}$/);
    await route.fulfill({
      json: {
        access_token: accessToken,
        token_type: "Bearer",
        expires_in: 3600,
        scope: "openid profile",
        id_token: idToken,
      },
      headers: { "Access-Control-Allow-Origin": "*" },
    });
  });
  await page.goto(`/auth/callback?code=test-code&state=${encodeURIComponent(state!)}`);
  await expect(page).toHaveURL(`${baseURL}/`);
  await expect(page.getByRole("button", { name: /当前 com\.apple\.finder/ })).toBeVisible();
});

test("异常登录回调提供可恢复的错误界面", async ({ page }) => {
  await identityRoutes(page);
  await page.goto("/auth/callback?code=invalid&state=missing-state");
  await expect(page.getByRole("heading", { name: "无法确认这次登录" })).toBeVisible();
  await page.getByRole("link", { name: "返回登录" }).click();
  await expect(page).toHaveURL(/\/login/);
});
