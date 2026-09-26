import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RecordValue } from "@/components/records/RecordValue";

describe("RecordValue", () => {
  it("uses the foreground application renderer for its exact type and version", () => {
    render(
      <RecordValue
        type="desktop.application.foreground"
        version={1}
        value={{
          device_id: "mac-studio",
          application: {
            platform: "macos",
            id_kind: "bundle_id",
            id: "com.apple.finder",
            display_name: "Finder",
          },
        }}
      />,
    );

    expect(screen.getByText(/^com\.apple\.finder/)).toBeVisible();
    expect(screen.getByText("Finder")).toBeVisible();
    expect(screen.getByText("mac-studio")).toBeVisible();
    expect(screen.getByText(/MACOS · Bundle ID/)).toBeVisible();
  });

  it("renders the foreground window title as its own record", () => {
    render(
      <RecordValue
        type="desktop.window.foreground"
        version={1}
        value={{ device_id: "mac-studio", window: { title: "Downloads" } }}
      />,
    );

    expect(screen.getByText("Downloads")).toBeVisible();
    expect(screen.getByText("mac-studio")).toBeVisible();
  });

  it("marks a malformed known value as failed and preserves escaped JSON", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const { container } = render(
      <RecordValue
        type="desktop.application.foreground"
        version={1}
        value={{ malformed: "observation" }}
      />,
    );

    expect(screen.getByRole("alert")).toHaveTextContent("记录解析失败");
    expect(container.querySelector("pre")).toHaveTextContent('"malformed": "observation"');
  });

  it("never executes unknown values as HTML", () => {
    const value = "<script>window.untrustedExecuted = true</script>";
    render(<RecordValue type="future.record" version={7} value={value} />);

    expect(screen.getByText(/window\.untrustedExecuted/)).toBeVisible();
    expect(document.querySelector("script")).toBeNull();
    expect("untrustedExecuted" in window).toBe(false);
  });
});
