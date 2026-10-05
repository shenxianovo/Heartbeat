import { expect, it } from "vitest";
import { objectHref, objectKind, objectName } from "./model";

it("labels objects without a native identifier by their known name or identity", () => {
  expect(objectName({ id: "identity", name: null, key: null })).toBe("identity");
  expect(objectName({ id: "identity", name: "Timeline", key: null })).toBe("Timeline");
  expect(objectKind(null)).toBe("对象");
});

it("keeps every context on descent and restores the parent scope on return", () => {
  const friend = new URL(objectHref("friend", undefined, ["account", "world"]), "http://local");
  expect(friend.searchParams.getAll("context")).toEqual(["account", "world"]);
  const parent = new URL(
    objectHref("world", undefined, ["account", "world", "friend"]),
    "http://local",
  );
  expect(parent.searchParams.getAll("context")).toEqual(["account"]);
  expect(objectHref("account", undefined, ["account", "world"])).toBe("/objects/account");
});
