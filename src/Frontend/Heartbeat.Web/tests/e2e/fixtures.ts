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
};

export const statusTrack = {
  ...desktopTrack,
  id: "019e0000-0000-7000-8000-000000000003",
  type: "desktop.observation.status",
};

export const desktopObject = {
  id: "019e0000-0000-7000-8000-000000000090",
  namespace: "device",
  key: "test-mac",
  name: "测试 Mac",
  roles: ["device"],
};
export const desktopPath = `/objects/${desktopObject.id}`;
export const deviceReference = { ...desktopObject, role: "device" };
export function appReference(key: string) {
  return {
    id:
      key === "com.apple.finder"
        ? "019e0000-0000-7000-8000-000000000091"
        : "019e0000-0000-7000-8000-000000000092",
    namespace: "app.macos.bundle_id",
    key,
    name: key,
    role: "application",
    roles: ["application"],
  };
}
function record(
  id: string,
  value: unknown,
  point = false,
  minutesAgo = 30,
  objects = [deviceReference],
) {
  return {
    id,
    startedAt: new Date(Date.now() - minutesAgo * 60000).toISOString(),
    endedAt: point ? null : new Date(Date.now() - (minutesAgo - 5) * 60000).toISOString(),
    observedAt: null,
    receivedAt: new Date().toISOString(),
    value,
    objects,
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
  const catalog = [
    desktopObject,
    appReference("com.apple.finder"),
    appReference("com.microsoft.VSCode"),
  ];
  await page.route("**/api/v1/objects**", (route) => {
    const path = new URL(route.request().url()).pathname;
    const item = catalog.find((item) => path === `/api/v1/objects/${item.id}`);
    return route.fulfill({ json: item ?? { objects: options.empty ? [] : catalog } });
  });
  await page.route("**/api/v1/tracks**", async (route) => {
    const url = new URL(route.request().url());
    requests.push(url);
    expect(route.request().headers().authorization).toBe(`Bearer ${accessToken}`);
    if (options.fail) return route.fulfill({ status: 503, json: { title: "暂时不可用" } });
    const focus = url.searchParams.get("objectId");
    const device = !focus || focus === desktopObject.id;
    if (url.pathname === "/api/v1/tracks")
      return route.fulfill({
        json: {
          tracks: options.empty
            ? []
            : device
              ? [desktopTrack, statusTrack, customTrack]
              : [desktopTrack],
        },
      });
    if (url.pathname.endsWith("/point-counts")) {
      const from = Date.parse(url.searchParams.get("from")!);
      const to = Date.parse(url.searchParams.get("to")!);
      const bucketSeconds = Number(url.searchParams.get("bucketSeconds"));
      const at = Date.now() - 30 * 60000;
      const index = Math.floor((at - from) / (bucketSeconds * 1000));
      return route.fulfill({
        json: {
          track: customTrack,
          from: new Date(from).toISOString(),
          to: new Date(to).toISOString(),
          bucketSeconds,
          buckets:
            at >= from && at < to && device
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
    }
    const later = url.searchParams.has("cursor");
    const track = url.pathname.includes(customTrack.id)
      ? customTrack
      : url.pathname.includes(statusTrack.id)
        ? statusTrack
        : desktopTrack;
    const records =
      track === customTrack
        ? [
            record(
              "019e0000-0000-7000-8000-000000000023",
              { note: "<script>window.untrustedExecuted=true</script>", count: 42 },
              true,
            ),
          ]
        : track === statusTrack
          ? [
              record(
                "019e0000-0000-7000-8000-000000000024",
                {
                  capability: "application",
                  state: "permission_required",
                  reason: "screen-recording",
                },
                false,
                25,
              ),
            ]
          : later
            ? [
                record("019e0000-0000-7000-8000-000000000022", {}, false, 10, [
                  deviceReference,
                  appReference("com.microsoft.VSCode"),
                ]),
              ]
            : [
                record("019e0000-0000-7000-8000-000000000020", {}, false, 30, [
                  deviceReference,
                  appReference("com.apple.finder"),
                ]),
                record(
                  "019e0000-0000-7000-8000-000000000021",
                  { unexpected: "malformed-observation" },
                  false,
                  20,
                ),
              ];
    return route.fulfill({
      json: {
        track,
        records: records.filter(
          (record) => !focus || record.objects.some((item) => item.id === focus),
        ),
        nextCursor: track === desktopTrack && !later ? "next-page-test-cursor" : null,
      },
    });
  });
  return requests;
}
