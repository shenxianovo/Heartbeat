import { describe, expect, it } from "vitest";
import type { Version } from "@/api/types";
import { observation } from "@/test/fixtures";
import {
  BUNDLE_AT,
  collapse,
  edgeGeometry,
  grow,
  NODE_H,
  nextFromBundle,
  pathToRoot,
  plan,
  reveal,
  REVEAL,
  SHOW_FIRST,
  start,
  type Neighbor,
  type Payload,
  type Resolved,
  type Side,
} from "./weave";

function version(entityId: string): Version {
  return {
    entityId,
    versionId: `${entityId}-v1`,
    previousVersionId: null,
    category: "business",
    schemaVersionId: "s",
    recordedAt: "2026-10-30T00:00:00Z",
    content: {},
  };
}

const entity = (entityId: string): Payload => ({ kind: "entity", version: version(entityId), observation: null });
const resolved = (entityId: string, side: Side, label = "x"): Resolved => ({ payload: entity(entityId), side, label });
const neighbor = (entityId: string, side: Side, label: string, time = 0): Neighbor => ({
  version: version(entityId),
  side,
  label,
  typeName: "时段",
  time,
});

describe("plan", () => {
  it("同一边、同一个标签、同一类型超过上限时，只直接长出最近的几个，其余收成一束", () => {
    const many = Array.from({ length: BUNDLE_AT + 4 }, (_, i) => neighbor(`p${i}`, "left", `members[${i}]`, i));
    const { direct, bundles } = plan([...many, neighbor("o", "left", "来源")]);
    expect(direct.map((n) => n.version.entityId)).toEqual([...["p9", "p8", "p7"].slice(0, SHOW_FIRST), "o"]);
    expect(bundles).toHaveLength(1);
    expect(bundles[0]).toHaveLength(BUNDLE_AT + 4 - SHOW_FIRST);
  });
});

describe("grow", () => {
  it("上游长在左栏，下游长在右栏，同一栏不重叠", () => {
    const weave = grow(
      start(entity("qq")),
      "qq",
      [resolved("a", "left"), resolved("b", "left"), resolved("k", "right")],
      [],
    );
    const at = (key: string) => weave.nodes.find((n) => n.key === key)!;
    expect(at("qq")).toMatchObject({ col: 0, expanded: true });
    expect(at("a").col).toBe(-1);
    expect(at("k").col).toBe(1);
    expect(Math.abs(at("a").y - at("b").y)).toBeGreaterThanOrEqual(NODE_H);
  });

  it("一份 Observation 画成结，键是它的 EntityId；内容是经历时并进经历，键是内容的 EntityId", () => {
    const knot: Payload = { kind: "knot", observation: observation({ entityId: "obs" }), inputSet: null };
    const merged: Payload = {
      kind: "entity",
      version: version("period"),
      observation: observation({ entityId: "obs2" }),
    };
    const weave = grow(
      start(entity("qq")),
      "qq",
      [
        { payload: knot, side: "right", label: "依据" },
        { payload: merged, side: "left", label: "localApplication" },
      ],
      [],
    );
    expect(weave.nodes.map((n) => [n.key, n.kind])).toEqual([
      ["qq", "entity"],
      ["obs", "knot"],
      ["period", "entity"],
    ]);
  });

  it("已经在图上的只连一条线；解析后是同一个节点的只算一次", () => {
    let weave = grow(start(entity("qq")), "qq", [resolved("k", "right")], []);
    weave = grow(
      weave,
      "k",
      [resolved("qq", "left", "依据"), resolved("p", "right", "内容"), resolved("p", "right")],
      [],
    );
    expect(weave.nodes.map((n) => n.key).sort()).toEqual(["k", "p", "qq"]);
    expect(weave.edges).toEqual([
      { from: "qq", to: "k", label: "x" },
      { from: "k", to: "p", label: "内容" },
    ]);
  });
});

describe("reveal", () => {
  it("每次从线束里放出几个，挂在线束的父节点下面；放完时线束和它的线一起消失", () => {
    const members = Array.from({ length: REVEAL + 2 }, (_, i) => neighbor(`p${i}`, "left", "x", i));
    let weave = grow(start(entity("qq")), "qq", [], [members]);
    const key = weave.nodes.find((n) => n.kind === "bundle")!.key;
    const resolve = (list: Neighbor[]) => list.map((n) => resolved(n.version.entityId, n.side));
    weave = reveal(weave, key, resolve(nextFromBundle(weave, key)));
    expect(weave.nodes.filter((n) => n.kind === "entity")).toHaveLength(1 + REVEAL);
    expect(weave.nodes.find((n) => n.key === key)).toMatchObject({ members: { length: 2 } });
    weave = reveal(weave, key, resolve(nextFromBundle(weave, key)));
    expect(weave.nodes.some((n) => n.key === key)).toBe(false);
    expect(weave.edges.some((e) => e.to === key)).toBe(false);
    expect(weave.nodes.filter((n) => n.parent === "qq")).toHaveLength(members.length);
  });
});

describe("collapse、pathToRoot 和 edgeGeometry", () => {
  it("收起一个节点时，从它长出来的整枝一起移除", () => {
    let weave = grow(start(entity("qq")), "qq", [resolved("k", "right"), resolved("c", "left")], []);
    weave = grow(weave, "k", [resolved("p", "right"), resolved("o", "left", "来源")], []);
    expect(pathToRoot(weave, "p")).toEqual(["p", "k", "qq"]);
    weave = collapse(weave, "k");
    expect(weave.nodes.map((n) => n.key).sort()).toEqual(["c", "k", "qq"]);
    expect(weave.nodes.find((n) => n.key === "k")).toMatchObject({ expanded: false });
  });

  it("连线从左栏节点的右边画到右栏节点的左边，标签写在长出来的那一端", () => {
    const weave = grow(start(entity("qq")), "qq", [resolved("a", "left"), resolved("k", "right")], []);
    const at = (key: string) => weave.nodes.find((n) => n.key === key)!;
    const right = edgeGeometry(at("qq"), at("k"));
    const left = edgeGeometry(at("qq"), at("a"));
    expect(right.label.anchor).toBe("end");
    expect(left.label.anchor).toBe("start");
    expect(right.d.startsWith("M104 ")).toBe(true);
  });
});
