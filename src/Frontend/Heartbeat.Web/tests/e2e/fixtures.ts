import { expect, type Page } from "@playwright/test";

export const authority = "https://identity.heartbeat.test";
export const clientId = "heartbeat-web-test";
export const owner = "00000000-0000-4000-8000-000000000011";
export const accessToken = "test-recording-access-token";
export const userStorageKey = `oidc.user:${authority}:${clientId}`;

export const desktopTrack = {
  id: "019e0000-0000-7000-8000-000000000001",
  collectorId: "019e0000-0000-7000-8000-000000000010",
  collectorKey: "heartbeat.collector.desktop.macos",
  collectorTarget: "test-mac",
  collectorDisplayName: "测试 Mac",
  type: "desktop.application.foreground",
  version: 1,
  timeMode: "range",
  endMode: "explicit",
  createdAt: "2026-09-12T00:00:00Z",
};

export const customTrack = {
  ...desktopTrack,
  id: "019e0000-0000-7000-8000-000000000002",
  collectorId: "019e0000-0000-7000-8000-000000000011",
  collectorKey: "example.custom",
  collectorDisplayName: "自定义来源",
  type: "example.observation",
  timeMode: "point",
  endMode: null,
};

export const statusTrack = {
  ...desktopTrack,
  id: "019e0000-0000-7000-8000-000000000003",
  type: "desktop.observation.status",
};

function record(id: string, value: unknown, point = false, minutesAgo = 30) {
  const startedAt = new Date(Date.now() - minutesAgo * 60_000).toISOString();
  return {
    id,
    startedAt,
    endedAt: point ? null : new Date(Date.now() - (minutesAgo - 5) * 60_000).toISOString(),
    observedAt: null,
    receivedAt: new Date().toISOString(),
    value,
  };
}

export async function seedSession(page: Page) {
  await page.addInitScript(
    ({ key, user }) => {
      if (!sessionStorage.getItem("heartbeat-test:seeded")) {
        sessionStorage.setItem("heartbeat-test:seeded", "true");
        sessionStorage.setItem(key, JSON.stringify(user));
      }
    },
    {
      key: userStorageKey,
      user: {
        access_token: accessToken,
        token_type: "Bearer",
        scope: "openid profile",
        expires_at: Math.floor(Date.now() / 1000) + 3600,
        profile: { sub: owner, name: "测试用户", preferred_username: "tester" },
      },
    },
  );
}

export async function identityRoutes(page: Page) {
  await page.route(`${authority}/.well-known/openid-configuration`, (route) =>
    route.fulfill({
      json: {
        issuer: authority,
        authorization_endpoint: `${authority}/authorize`,
        token_endpoint: `${authority}/token`,
        jwks_uri: `${authority}/jwks`,
        response_types_supported: ["code"],
        subject_types_supported: ["public"],
        id_token_signing_alg_values_supported: ["RS256"],
        code_challenge_methods_supported: ["S256"],
      },
      headers: { "Access-Control-Allow-Origin": "*" },
    }),
  );
  await page.route(`${authority}/authorize?**`, (route) =>
    route.fulfill({ contentType: "text/html", body: "<h1>Test identity provider</h1>" }),
  );
}

export async function recordingRoutes(
  page: Page,
  options: { empty?: boolean; fail?: boolean } = {},
) {
  const requests: URL[] = [];
  await page.route("**/api/v1/tracks**", async (route) => {
    const url = new URL(route.request().url());
    requests.push(url);
    expect(route.request().headers().authorization).toBe(`Bearer ${accessToken}`);
    if (options.fail) {
      await route.fulfill({ status: 503, json: { title: "暂时不可用" } });
      return;
    }
    if (url.pathname === "/api/v1/tracks") {
      await route.fulfill({
        json: { tracks: options.empty ? [] : [desktopTrack, statusTrack, customTrack] },
      });
      return;
    }
    if (url.pathname === `/api/v1/tracks/${customTrack.id}/point-counts`) {
      const from = Date.parse(url.searchParams.get("from")!);
      const to = Date.parse(url.searchParams.get("to")!);
      const bucketSeconds = Number(url.searchParams.get("bucketSeconds"));
      const at = Date.now() - 30 * 60_000;
      const index = Math.floor((at - from) / (bucketSeconds * 1000));
      await route.fulfill({
        json: {
          track: customTrack,
          from: new Date(from).toISOString(),
          to: new Date(to).toISOString(),
          bucketSeconds,
          buckets:
            at >= from && at < to
              ? [
                  {
                    index,
                    startedAt: new Date(from + index * bucketSeconds * 1000).toISOString(),
                    endedAt: new Date(
                      Math.min(to, from + (index + 1) * bucketSeconds * 1000),
                    ).toISOString(),
                    count: 1,
                  },
                ]
              : [],
        },
      });
      return;
    }
    if (url.pathname === `/api/v1/tracks/${customTrack.id}/records`) {
      await route.fulfill({
        json: {
          track: customTrack,
          records: [
            record(
              "019e0000-0000-7000-8000-000000000023",
              { note: "<script>window.untrustedExecuted=true</script>", count: 42 },
              true,
            ),
          ],
          nextCursor: null,
        },
      });
      return;
    }
    if (url.pathname === `/api/v1/tracks/${statusTrack.id}/records`) {
      await route.fulfill({
        json: {
          track: statusTrack,
          records: [
            record(
              "019e0000-0000-7000-8000-000000000024",
              {
                device_id: "test-mac",
                capability: "application",
                state: "permission_required",
                reason: "screen-recording",
              },
              false,
              25,
            ),
          ],
          nextCursor: null,
        },
      });
      return;
    }
    const later = url.searchParams.has("cursor");
    await route.fulfill({
      json: {
        track: desktopTrack,
        records: later
          ? [
              record(
                "019e0000-0000-7000-8000-000000000022",
                {
                  device_id: "test-mac",
                  application: {
                    platform: "macos",
                    id_kind: "bundle_id",
                    id: "com.microsoft.VSCode",
                  },
                },
                false,
                10,
              ),
            ]
          : [
              record("019e0000-0000-7000-8000-000000000020", {
                device_id: "test-mac",
                application: { platform: "macos", id_kind: "bundle_id", id: "com.apple.finder" },
              }),
              record(
                "019e0000-0000-7000-8000-000000000021",
                { unexpected: "malformed-observation" },
                false,
                20,
              ),
            ],
        nextCursor: later ? null : "next-page-test-cursor",
      },
    });
  });
  return requests;
}
