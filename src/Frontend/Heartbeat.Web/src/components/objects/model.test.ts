import { expect, it } from "vitest";
import { objectHref } from "./model";

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
