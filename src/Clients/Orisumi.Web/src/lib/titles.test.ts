import { describe, expect, it } from "vitest";
import type { FieldDefinition } from "@/api/types";
import { businessTitle } from "./titles";

const string = (): FieldDefinition => ({ type: "string", description: "" });
const reference = (): FieldDefinition => ({ type: "reference", description: "" });

describe("businessTitle", () => {
  it("取第一个不是引用的文字字段", () => {
    const fields = { appVersionId: reference(), name: string() };
    expect(businessTitle({ appVersionId: "v1", name: "VS Code" }, fields, "periods")).toEqual({
      text: "VS Code",
      key: "name",
    });
  });

  it("本机应用取 bundle id，线索的顺序不影响", () => {
    const content = {
      identifiers: [
        { scheme: "macos.executable-path", value: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" },
        { scheme: "macos.bundle-id", value: "com.google.Chrome" },
      ],
    };
    expect(businessTitle(content, undefined, "local-applications")).toEqual({ text: "com.google.Chrome" });
  });

  it("没有 bundle id 也没有文字字段时没有名字", () => {
    expect(businessTitle({ identifiers: [] }, undefined, "local-applications")).toBeNull();
  });
});
