import { describe, expect, it } from "vitest";
import { isUuidv7, uuidv7 } from "./uuid";

describe("uuidv7", () => {
  it("生成符合 RFC 形状的 UUIDv7（版本位 7、变体位 8-b）", () => {
    for (let i = 0; i < 50; i++) {
      const id = uuidv7();
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
      expect(isUuidv7(id)).toBe(true);
    }
  });

  it("把毫秒时间戳编码在前 48 位", () => {
    const now = Date.parse("2026-10-17T02:00:00.000Z");
    const id = uuidv7(now);
    const hex = id.replaceAll("-", "");
    const tsMs = Number(BigInt("0x" + hex.slice(0, 12)));
    expect(tsMs).toBe(now);
  });

  it("拒绝非 UUIDv7 字符串", () => {
    expect(isUuidv7("not-a-uuid")).toBe(false);
    // UUIDv4 不算 v7
    expect(isUuidv7("0192b056-7300-4000-8000-000000000001")).toBe(false);
    // 变体位错误
    expect(isUuidv7("0192b056-7300-7000-0000-000000000001")).toBe(false);
  });
});
