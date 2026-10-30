import { describe, expect, it } from "vitest";
import type { Version } from "@/api/types";
import { observation } from "@/test/fixtures";
import {
  BUNDLE_AT,
  collapse,
  edgeGeometry,
  grow,
  INPUT,
  link,
  linked,
  NODE_H,
  nextFromBundle,
  pathToRoot,
  plan,
  reveal,
  REVEAL,
  SHOW_FIRST,
  SOURCE,
  start,
  unlink,
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
const neighbor = (entityId: string, side: Side, label: string, time = 0, typeName = "时段"): Neighbor => ({
  version: version(entityId),
  side,
  label,
  typeName,
  time,
});

describe("plan", () => {
  it("同一边、同一个标签、同一类型超过上限时，只直接长出最近的几个，其余收成一束", () => {
    const many = Array.from({ length: BUNDLE_AT + 4 }, (_, i) => neighbor(`p${i}`, "left", `members[${i}]`, i));
    const { direct, bundles } = plan([...many, neighbor("o", "left", "来源")]);
    expect(direct.map((n) => n.version.entityId)).toEqual([...["p9", "p8", "p7"].slice(0, SHOW_FIRST), "o"]);
    expect(bundles).toHaveLength(1);
    expect(bundles[0]).toMatchObject({ more: true, members: { length: BUNDLE_AT + 4 - SHOW_FIRST } });
  });

  it("依据按类型分组：只有一个的类型直接长出，同一类型有两个以上的全部收成一叠", () => {
    const products = ["qq", "lark", "chrome"].map((id, i) => neighbor(id, "left", INPUT, i, "应用产品"));
    const { direct, bundles } = plan([
      neighbor("preview", "left", INPUT, 0, "本机应用"),
      ...products,
      neighbor("period", "left", INPUT, 0, "前台应用时段"),
    ]);
    expect(direct.map((n) => n.version.entityId)).toEqual(["preview", "period"]);
    expect(bundles).toHaveLength(1);
    expect(bundles[0]).toMatchObject({ more: false, members: { length: 3 } });
  });

  it("以它为输入的陈述不算依据，照常长出", () => {
    const { direct, bundles } = plan([neighbor("a", "right", INPUT), neighbor("b", "right", INPUT)]);
    expect(direct).toHaveLength(2);
    expect(bundles).toHaveLength(0);
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
    let weave = grow(start(entity("qq")), "qq", [], [{ members, more: false }]);
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

  it("放出来的节点排在线束上面，线束留在这一组的末尾", () => {
    const members = Array.from({ length: REVEAL + 2 }, (_, i) => neighbor(`p${i}`, "left", "x", i));
    let weave = grow(start(entity("qq")), "qq", [resolved("a", "left")], [{ members, more: true }]);
    const key = weave.nodes.find((n) => n.kind === "bundle")!.key;
    weave = reveal(
      weave,
      key,
      nextFromBundle(weave, key).map((n) => resolved(n.version.entityId, n.side)),
    );
    const column = weave.nodes.filter((n) => n.col === -1).sort((a, b) => a.y - b.y);
    expect(column[0]!.key).toBe("a");
    expect(column.at(-1)!.key).toBe(key);
  });
});

describe("排布", () => {
  it("一个节点连同它长出来的整枝占一段高度，同一栏的兄弟让开，不和别的枝交错", () => {
    let weave = grow(
      start(entity("knot")),
      "knot",
      [resolved("qq", "left"), resolved("preview", "left"), resolved("lark", "left")],
      [],
    );
    weave = grow(
      weave,
      "preview",
      ["p1", "p2", "p3", "p4"].map((id) => resolved(id, "left")),
      [],
    );
    const at = (key: string) => weave.nodes.find((n) => n.key === key)!;
    const periods = ["p1", "p2", "p3", "p4"].map((key) => at(key).y);
    const band = { top: Math.min(...periods), bottom: Math.max(...periods) };
    for (const sibling of ["qq", "lark"]) {
      const y = at(sibling).y;
      expect(y + NODE_H / 2 <= band.top - NODE_H / 2 || y - NODE_H / 2 >= band.bottom + NODE_H / 2).toBe(true);
    }
    expect(at("qq").y).toBeLessThan(at("preview").y);
    expect(at("lark").y).toBeGreaterThan(at("preview").y);
    expect(at("preview").y).toBe((band.top + band.bottom) / 2);
  });

  it("同一栏里的节点不重叠，回到父节点那一栏的枝也一样", () => {
    let weave = grow(start(entity("knot")), "knot", [resolved("preview", "left"), resolved("lark", "left")], []);
    weave = grow(weave, "preview", [resolved("other", "right")], []);
    const column = weave.nodes.filter((n) => n.col === 0).map((n) => n.y);
    expect(Math.abs(column[0]! - column[1]!)).toBeGreaterThanOrEqual(NODE_H);
  });
});

describe("link 和 unlink", () => {
  it("从结上长出来源不改它的展开状态，再收起时连同长出来的整枝移除", () => {
    let weave = link(start(entity("knot")), "knot", [resolved("observer", "left", SOURCE)]);
    expect(weave.nodes.find((n) => n.key === "knot")).toMatchObject({ expanded: false });
    expect(linked(weave, "knot", "observer")).toBe(true);
    weave = grow(weave, "observer", [resolved("other", "right")], []);
    weave = unlink(weave, "knot", "observer");
    expect(weave.nodes.map((n) => n.key)).toEqual(["knot"]);
    expect(weave.edges).toEqual([]);
  });

  it("来源已经在图上时只连一条虚线，收起时只去掉那条线", () => {
    let weave = grow(start(entity("root")), "root", [resolved("knot", "right"), resolved("observer", "left")], []);
    weave = link(weave, "knot", [resolved("observer", "left", SOURCE)]);
    expect(linked(weave, "knot", "observer")).toBe(true);
    weave = unlink(weave, "knot", "observer");
    expect(weave.nodes.map((n) => n.key).sort()).toEqual(["knot", "observer", "root"]);
    expect(linked(weave, "knot", "observer")).toBe(false);
  });

  it("结是从它的 Observer 长出来的时，那条线保留", () => {
    const weave = grow(start(entity("observer")), "observer", [resolved("knot", "right")], []);
    expect(unlink(weave, "knot", "observer")).toBe(weave);
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
