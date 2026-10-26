import { describe, expect, it } from "vitest";
import { chooseSourceKind } from "./source";

function storage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, value),
    values,
  };
}

describe("chooseSourceKind", () => {
  it("默认读后端", () => {
    expect(chooseSourceKind("", storage())).toBe("api");
  });

  it("?source=demo 切到演示数据源，并在本标签页内保持", () => {
    const store = storage();
    expect(chooseSourceKind("?source=demo", store)).toBe("demo");
    expect(chooseSourceKind("", store)).toBe("demo");
    expect(chooseSourceKind("?source=api", store)).toBe("api");
    expect(chooseSourceKind("", store)).toBe("api");
  });

  it("存储不可用时照常选择", () => {
    const broken = {
      getItem: () => {
        throw new Error("blocked");
      },
      setItem: () => {
        throw new Error("blocked");
      },
    };
    expect(chooseSourceKind("?source=demo", broken)).toBe("demo");
    expect(chooseSourceKind("", broken)).toBe("api");
  });
});
