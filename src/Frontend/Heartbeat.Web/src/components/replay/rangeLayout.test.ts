import { expect, it } from "vitest";
import { layoutRanges } from "./rangeLayout";

it("excludes a completed interval at the window start without dropping a point at that instant", () => {
  const base = {
    id: "ended",
    startedAt: "2026-09-15T07:00:00Z",
    endedAt: "2026-09-15T08:00:00Z",
    value: null,
    observedAt: null,
    receivedAt: "2026-09-15T08:00:00Z",
  };
  const result = layoutRanges(
    [base, { ...base, id: "point", startedAt: base.endedAt, endedAt: null }],
    Date.parse("2026-09-15T08:00:00Z"),
    Date.parse("2026-09-15T09:00:00Z"),
  );
  expect(result.items.map((item) => item.record.id)).toEqual(["point"]);
});
