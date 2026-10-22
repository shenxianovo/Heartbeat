import { describe, expect, it } from "vitest";
import { formatDateTime, formatDuration } from "./time";

describe("time formatting", () => {
  it("null 时间显式呈现为未知，不用当前时间填补", () => {
    expect(formatDateTime(null)).toBe("未知");
    expect(formatDateTime(undefined)).toBe("未知");
  });

  it("无效时间字符串返回占位", () => {
    expect(formatDateTime("not-a-date")).toBe("无效时间");
  });

  it("时长：相等边界为时间点，区间为人类可读时长", () => {
    const start = "2026-10-17T01:00:00Z";
    expect(formatDuration(start, start)).toBe("时间点");
    expect(formatDuration(start, "2026-10-17T03:02:00Z")).toBe("2 小时 2 分");
    expect(formatDuration(start, null)).toBe("进行中 · 结束未知");
  });
});
