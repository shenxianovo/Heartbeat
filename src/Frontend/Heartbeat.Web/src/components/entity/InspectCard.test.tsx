import { describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { InspectCard } from "./InspectCard";
import type { EntityResponse } from "@/api/types";

// InspectCard 只用 Link 做引用跳转；测试里替换成普通锚点。
vi.mock("@tanstack/react-router", () => ({
  Link: ({ to, params, children, ...rest }: Record<string, unknown>) => (
    <a href={`${String(to)}?${JSON.stringify(params)}`} {...rest}>
      {children as ReactNode}
    </a>
  ),
}));

const id = "0192b056-7300-7000-8000-000000000001";

const observation: EntityResponse = {
  category: "observation",
  references: {
    observerId: "0192b056-7300-7000-8000-000000000002",
    contentId: "0192b056-7300-7000-8000-000000000003",
    schemaId: "0192b056-7300-7000-8000-000000000004",
  },
  properties: {
    startAt: "2026-10-17T01:00:00Z",
    endAt: null,
    timeZone: null,
  },
};

function renderCard() {
  return render(<InspectCard id={id} entity={observation} bookmarked={false} onToggleBookmark={() => undefined} />);
}

describe("InspectCard", () => {
  it("展示类别、三个具名引用与显式 null 的事实结束时间", () => {
    renderCard();
    expect(screen.getByText("观测")).toBeInTheDocument();
    expect(screen.getByText("observerId")).toBeInTheDocument();
    expect(screen.getByText("contentId")).toBeInTheDocument();
    expect(screen.getByText("schemaId")).toBeInTheDocument();
    // endAt: null → 未知；进行中时长标记
    expect(screen.getAllByText("未知").length).toBeGreaterThan(0);
    expect(screen.getByText(/进行中 · 结束未知/)).toBeInTheDocument();
  });

  it("原始 JSON 标签页展示完整信封", async () => {
    const user = userEvent.setup();
    renderCard();
    await user.click(screen.getByRole("tab", { name: "原始 JSON" }));
    expect(await screen.findByText(/"category": "observation"/)).toBeInTheDocument();
  });
});
