import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RecordValue } from "@/components/records/RecordValue";

describe("RecordValue", () => {
  it("uses the foreground application renderer for its exact type and version", () => {
    render(
      <RecordValue
        type="desktop.application.foreground"
        version={1}
        value={{}}
        objects={[
          {
            id: "finder",
            role: "application",
            namespace: "app.macos.bundle_id",
            key: "com.apple.finder",
            name: "Finder",
          },
        ]}
      />,
    );

    expect(screen.getByText("Finder")).toBeVisible();
  });

  it("renders the foreground window title as its own record", () => {
    render(
      <RecordValue
        type="desktop.window.foreground"
        version={1}
        objects={[]}
        value={{ window: { title: "Downloads" } }}
      />,
    );

    expect(screen.getByText("Downloads")).toBeVisible();
  });

  it("marks a malformed known value as failed and preserves escaped JSON", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const { container } = render(
      <RecordValue
        type="desktop.application.foreground"
        version={1}
        objects={[]}
        value={{ malformed: "observation" }}
      />,
    );

    expect(screen.getByRole("alert")).toHaveTextContent("记录解析失败");
    expect(container.querySelector("pre")).toHaveTextContent('"malformed": "observation"');
  });

  it("never executes unknown values as HTML", () => {
    const value = "<script>window.untrustedExecuted = true</script>";
    render(<RecordValue type="future.record" version={7} value={value} objects={[]} />);

    expect(screen.getByText(/window\.untrustedExecuted/)).toBeVisible();
    expect(document.querySelector("script")).toBeNull();
    expect("untrustedExecuted" in window).toBe(false);
  });
});
