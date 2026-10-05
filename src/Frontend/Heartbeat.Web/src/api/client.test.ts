import { afterEach, expect, it, vi } from "vitest";
import { fetchObjects } from "./client";

afterEach(() => vi.unstubAllGlobals());

it("reads all catalog pages while retaining every object condition", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          objects: [{ id: "timeline", namespace: null, key: null, name: null, roles: [] }],
          nextCursor: "timeline",
        }),
      ),
    )
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          objects: [
            { id: "device", namespace: "device", key: "mac", name: "Mac", roles: ["device"] },
          ],
          nextCursor: null,
        }),
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const result = await fetchObjects("token", undefined, ["account", "world"], "source");
  expect(result.objects.map((item) => item.id)).toEqual(["timeline", "device"]);
  for (const [path] of fetch.mock.calls) {
    const url = new URL(String(path), "http://local");
    expect(url.searchParams.getAll("contextObjectIds")).toEqual(["account", "world"]);
    expect(url.searchParams.get("objectId")).toBe("source");
  }
  expect(new URL(String(fetch.mock.calls[1]![0]), "http://local").searchParams.get("after")).toBe(
    "timeline",
  );
});
