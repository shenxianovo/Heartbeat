import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readAllPages } from "./client";
import { createDemoSource } from "./demo";
import type { EntitySchema, Observation, Version } from "./types";

const start = Date.parse("2026-10-25T08:00:00.000Z");

function demo() {
  let clock = start;
  let seed = 1;
  const random = () => {
    seed = (seed * 16807) % 2147483647;
    return (seed - 1) / 2147483646;
  };
  const source = createDemoSource({ now: () => clock, random });
  return { source, tick: (ms: number) => (clock += ms) };
}

describe("演示数据源", () => {
  beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout"] }));
  afterEach(() => vi.useRealTimers());

  async function run<T>(promise: Promise<T>): Promise<T> {
    // 拒绝留给调用方的 await 处理，推进计时器期间不算未处理。
    promise.catch(() => undefined);
    await vi.runAllTimersAsync();
    return promise;
  }

  it("listObservations 只返回和范围重叠的 Observation，并按开始边界分页", async () => {
    const { source } = demo();
    const from = new Date(start - 3600_000).toISOString();
    const to = new Date(start).toISOString();
    const pages: Version[][] = [];
    const all = await run(
      readAllPages(async (after) => {
        const page = source.listObservations({ from, to, limit: 50, ...(after ? { after } : {}) });
        await vi.runAllTimersAsync();
        const result = await page;
        pages.push(result.versions);
        return result;
      }),
    );
    expect(all.length).toBeGreaterThan(50);
    expect(pages[0]).toHaveLength(50);
    for (const version of all as Version<Observation>[]) {
      const s = version.content.startAt ? Date.parse(version.content.startAt) : null;
      const e = version.content.endAt ? Date.parse(version.content.endAt) : null;
      const overlaps = s !== null && e !== null ? s < Date.parse(to) && e >= Date.parse(from) : true;
      expect(overlaps).toBe(true);
    }
  });

  it("时间推进后产生新的写入", async () => {
    const { source, tick } = demo();
    const range = () => ({
      from: new Date(start - 120_000).toISOString(),
      to: new Date(start + 600_000).toISOString(),
    });
    const before = await run(source.listObservations({ ...range(), limit: 1000 }));
    tick(60_000);
    const after = await run(source.listObservations({ ...range(), limit: 1000 }));
    const changed = after.versions.filter(
      (v) => !before.versions.some((b) => b.entityId === v.entityId && b.versionId === v.versionId),
    );
    expect(changed.length).toBeGreaterThan(0);
  });

  it("推断引用的已删除版本读不到，用户的否定出现在推断的被引用中", async () => {
    const { source } = demo();
    const schemas = await run(source.listEntities({ category: "entity_schema", limit: 1000 }));
    const inferenceSchema = schemas.versions.find(
      (v) => (v.content as EntitySchema).resourceName === "activity-inference-observers",
    )!;
    const observers = await run(source.listEntities({ resourceName: "activity-inference-observers" }));
    const observer = observers.versions[0]!;
    expect(observer.schemaVersionId).toBe(inferenceSchema.versionId);

    const made = await run(source.listReferrers(observer.entityId, {}));
    const inferences = made.referrers.map((r) => r.version as Version<Observation>);
    const withInputs = inferences.find((v) => v.content.inputSetVersionId)!;
    const inputSet = await run(source.getVersion(withInputs.content.inputSetVersionId!));
    const members = (inputSet.content as { members: string[] }).members;
    const results = await run(Promise.allSettled(members.map((m) => source.getVersion(m))));
    expect(results.filter((r) => r.status === "rejected")).toHaveLength(1);

    const denials = await run(source.listReferrers(withInputs.entityId, {}));
    expect(denials.referrers).toHaveLength(1);
    expect(denials.referrers[0]!.references[0]!.path).toBe("/content/target");
  });

  it("删除后读取返回 410，仍在使用的 EntitySchema 不能删除", async () => {
    const { source } = demo();
    const observers = await run(source.listEntities({ resourceName: "foreground-state-observers" }));
    const observer = observers.versions[0]!;
    const schema = await run(source.getVersion(observer.schemaVersionId!));
    await expect(run(source.deleteEntity(schema.entityId))).rejects.toMatchObject({ status: 409 });

    await run(source.deleteEntity(observer.entityId));
    await expect(run(source.getEntity(observer.entityId))).rejects.toMatchObject({ status: 410 });
    await expect(run(source.getVersion(observer.versionId))).rejects.toMatchObject({ status: 410 });
  });
});
