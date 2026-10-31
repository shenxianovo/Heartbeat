import { afterEach, expect, test, vi } from "vitest";
import { render, screen, waitFor, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from "@tanstack/react-router";
import { HostsPage } from "@/routes/hosts";
import type { Host, Instance } from "@/api/management";

const host: Host = {
  hostId: "019e1bbb-ec00-7000-8000-000000000001",
  name: "离线设备",
  kind: "device",
  revoked: false,
  connected: false,
  lastSeenAt: "2026-01-01T00:00:00Z",
};
const initial: Instance = {
  entityId: "019e1bbb-ec00-7000-8000-000000000002",
  hostId: host.hostId,
  instanceKey: "example",
  name: "测试 Observer",
  enabled: true,
  settings: {},
  revision: 2,
  removed: false,
  reportedAt: "2026-01-01T00:00:00Z",
  status: { state: "running", enabled: true, appliedRevision: 1, pendingChanges: 0 },
};
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("离线设备的旧运行报告显示未知，启用和设置仍可保存，在线操作不可用", async () => {
  let instance = structuredClone(initial);
  const writes: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === "/api/hosts") return Response.json({ hosts: [host] });
      if (path === "/api/observer-instances") return Response.json({ instances: [instance] });
      if (init?.method === "PATCH") {
        const body = JSON.parse(String(init.body)) as Partial<Instance>;
        writes.push(body);
        instance = { ...instance, ...body, revision: instance.revision + 1 };
        return Response.json(instance);
      }
      throw new Error(`Unexpected request: ${path}`);
    }),
  );
  const query = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createRouter({
    routeTree: createRootRoute({ component: HostsPage }),
    history: createMemoryHistory({ initialEntries: ["/"] }),
  });
  render(
    <QueryClientProvider client={query}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  const user = userEvent.setup();
  await screen.findByRole("button", { name: /测试 Observer/ });
  expect(screen.getByText("状态未知")).toBeInTheDocument();
  expect(screen.getByText("等待同步")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "安装" })).toBeDisabled();
  await user.click(screen.getByRole("switch", { name: "启用 测试 Observer" }));
  await waitFor(() => expect(writes).toEqual([{ enabled: false }]));
  await user.click(screen.getByRole("button", { name: /测试 Observer/ }));
  expect(screen.getByRole("button", { name: "更新" })).toBeDisabled();
  expect(screen.getByText("设备连接后才能操作")).toBeInTheDocument();
  const settings = screen.getByLabelText("设置");
  await user.clear(settings);
  await user.paste("[1]");
  expect(screen.getByText("要写成一个 JSON 对象")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "保存" })).toBeDisabled();
  await user.clear(settings);
  await user.paste('{"intervalSeconds":30}');
  await user.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(writes).toEqual([{ enabled: false }, { settings: { intervalSeconds: 30 } }]));
  query.clear();
});
