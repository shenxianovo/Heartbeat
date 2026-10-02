import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { SourceRows } from "./SourceRows";

const range = { from: "2026-09-01T00:00", to: "2026-09-02T00:00" };
const source = {
  id: "desktop",
  name: "测试 Mac",
  label: "Desktop",
  kind: "desktop" as const,
  summaryAvailable: true,
  milliseconds: 0,
  subjects: 0,
  invalid: 0,
  pending: false,
  failed: false,
  days: [{ from: Date.parse(range.from), to: Date.parse(range.to), milliseconds: 0 }],
};

it("keeps missing durations distinct from pending and failed queries", () => {
  const view = render(<SourceRows sources={[source]} range={range} />);
  expect(screen.getByRole("img", { name: "每日时长" })).toBeVisible();
  expect(screen.getByText("暂无记录")).toBeVisible();
  view.rerender(<SourceRows sources={[{ ...source, pending: true }]} range={range} />);
  expect(screen.queryByRole("img")).not.toBeInTheDocument();
  expect(screen.getByText("正在读取每日记录…")).toBeVisible();
  view.rerender(<SourceRows sources={[{ ...source, failed: true }]} range={range} />);
  expect(screen.queryByRole("img")).not.toBeInTheDocument();
  expect(screen.getByRole("alert")).toHaveTextContent("读取失败");
  const href = screen
    .getByRole("link", { name: "Desktop · 测试 Mac，查看详情" })
    .getAttribute("href")!;
  expect(new URL(href, "http://localhost").pathname).toBe("/objects/desktop");
});

it("keeps objects without an applicable duration summary accessible without claiming no records", () => {
  render(<SourceRows sources={[{ ...source, summaryAvailable: false }]} range={range} />);
  expect(screen.getByRole("link", { name: "Desktop · 测试 Mac，查看详情" })).toBeVisible();
  expect(screen.queryByText("暂无记录")).not.toBeInTheDocument();
  expect(screen.queryByText("0 个应用")).not.toBeInTheDocument();
  expect(screen.queryByRole("img", { name: "每日时长" })).not.toBeInTheDocument();
});
