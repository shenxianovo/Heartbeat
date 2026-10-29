import { describe, expect, it } from "vitest";
import type { ObserverActivity } from "@/lib/activity";
import { SECOND } from "@/lib/time";
import { createBubbleRenderer } from "./bubble";
import type { ViewInput } from "./common";
import { MAX_RINGS, renderRing, ringLayout } from "./ring";
import { renderStrip } from "./strip";

const now = Date.parse("2026-10-25T06:00:00.000Z");

function observer(key: string, overrides: Partial<ObserverActivity> = {}): ObserverActivity {
  return {
    key,
    entityId: key,
    name: key,
    category: "desktop",
    writes: [],
    seen: [],
    lastSeenAt: 0,
    lastActivityAt: now - 3600 * SECOND,
    observationCount: 1,
    segments: [
      { entityId: `${key}-o`, start: now - 3600 * SECOND, end: now - 1800 * SECOND, recordedAt: now - 1800 * SECOND },
    ],
    ...overrides,
  };
}

function input(observers: ObserverActivity[], overrides: Partial<ViewInput> = {}): ViewInput {
  return {
    width: 900,
    observers,
    now,
    selected: null,
    reduced: false,
    dayStart: Date.parse("2026-10-24T16:00:00.000Z"),
    observationCount: observers.length,
    expanded: false,
    ...overrides,
  };
}

describe("ringLayout", () => {
  it("写入多的靠外", () => {
    const rings = ringLayout([observer("a", { observationCount: 1 }), observer("b", { observationCount: 5 })]);
    expect(rings.map((r) => r.key)).toEqual(["a", "b"]);
  });

  it("超过 24 圈时保留 23 个，其余合并成最内的其他", () => {
    const many = Array.from({ length: 30 }, (_, i) => observer(`o${i}`, { observationCount: i }));
    const rings = ringLayout(many);
    expect(rings).toHaveLength(MAX_RINGS);
    expect(rings[0]!.key).toBe("other");
    expect(rings[0]!.segments).toHaveLength(7);
    expect(rings.at(-1)!.key).toBe("o29");
  });

  it("今天没有 Observation 的 Observer 不占圈", () => {
    expect(ringLayout([observer("a", { segments: [] })])).toEqual([]);
  });
});

describe("renderers", () => {
  const writer = observer("<b>前台</b>", {
    writes: [now - 2 * SECOND, now - 1 * SECOND],
    seen: [now - 2 * SECOND, now - 1 * SECOND],
    lastSeenAt: now - 1 * SECOND,
    lastActivityAt: now - 1 * SECOND,
  });
  const quiet = observer("quiet");

  it("名称经过转义", () => {
    for (const render of [renderStrip, renderRing, createBubbleRenderer()]) {
      const { markup } = render(input([writer, quiet], { selected: writer.key }));
      expect(markup).not.toContain("<b>");
    }
  });

  it("行：安静的 Observer 折叠成一行，展开后逐个列出", () => {
    const folded = renderStrip(input([writer, quiet]));
    expect(folded.markup).toContain("另外 1 个");
    expect(folded.markup).not.toContain('data-key="quiet"');
    const expanded = renderStrip(input([writer, quiet], { expanded: true }));
    expect(expanded.markup).toContain('data-key="quiet"');
    expect(expanded.markup).toContain("收起");
  });

  it("选中时其余 Observer 降低不透明度", () => {
    const { markup } = renderStrip(input([writer, quiet], { selected: "quiet", expanded: true }));
    expect(markup).toMatch(/data-key="&#60;b&#62;前台&#60;\/b&#62;" opacity="0.2"/);
  });
});
