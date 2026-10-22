import { describe, expect, it, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { IdSearchBar } from "./IdSearchBar";

const VALID_ID = "0192b056-7300-7000-8000-000000000001";

// TanStack Router 的 useNavigate 在单测里替换为捕获器。
const navigations: unknown[] = [];
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => (args: unknown) => {
    navigations.push(args);
  },
}));

describe("IdSearchBar", () => {
  beforeEach(() => {
    navigations.length = 0;
  });

  it("非法 UUIDv7 时禁用提交，合法时导航到实体页", async () => {
    const user = userEvent.setup();
    render(<IdSearchBar />);
    const button = screen.getByRole("button", { name: "观测" });
    expect(button).toBeDisabled();

    await user.type(screen.getByLabelText("实体 ID"), VALID_ID);
    expect(button).toBeEnabled();

    await user.click(button);
    await waitFor(() => expect(navigations).toHaveLength(1));
    expect(navigations[0]).toMatchObject({ to: "/entities/$id", params: { id: VALID_ID } });
  });

  it("UUIDv4 不会被当作合法 UUIDv7", async () => {
    const user = userEvent.setup();
    render(<IdSearchBar />);
    await user.type(screen.getByLabelText("实体 ID"), "0192b056-7300-4000-8000-000000000001");
    expect(screen.getByRole("button", { name: "观测" })).toBeDisabled();
  });
});
