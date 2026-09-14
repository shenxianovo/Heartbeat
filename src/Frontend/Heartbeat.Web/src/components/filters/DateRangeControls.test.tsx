import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { DateRangeControls } from "@/components/filters/DateRangeControls";

describe("DateRangeControls", () => {
  it("keeps an invalid range local and explains how to fix it", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    render(
      <DateRangeControls
        value={{ from: "2026-09-14T12:00", to: "2026-09-14T13:00" }}
        onApply={onApply}
      />,
    );

    const end = screen.getByLabelText("结束时间");
    await user.clear(end);
    await user.type(end, "2026-09-14T11:00");
    await user.click(screen.getByRole("button", { name: "应用范围" }));

    expect(onApply).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("结束时间需要晚于开始时间");
  });
});
