import { describe, expect, it } from "vitest";

import { safeReturnPath } from "@/auth/return-path";

describe("safeReturnPath", () => {
  it.each(["/\\evil.example", "/safe\n/../../evil", "//evil.example", "https://evil.example"])(
    "rejects a potentially cross-origin return path: %j",
    (value) => {
      expect(safeReturnPath(value)).toBe("/");
    },
  );

  it("keeps a local path with its query string", () => {
    expect(safeReturnPath("/records?day=today")).toBe("/records?day=today");
  });
});
