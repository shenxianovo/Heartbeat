import { describe, expect, it } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TooltipLayer, TooltipReading, useTooltip } from "./Tooltip";

function Target({ at }: { at: { x: number; y: number; pointerType?: string } }) {
  const tooltip = useTooltip();
  return (
    <button
      type="button"
      onPointerMove={() =>
        tooltip.show(<TooltipReading caption="09:00" value="12 条" />, {
          clientX: at.x,
          clientY: at.y,
          pointerType: at.pointerType,
        })
      }
      onPointerLeave={tooltip.hide}
    >
      目标
    </button>
  );
}

const tip = () => document.body.querySelector(".ui-tooltip") as HTMLElement | null;

describe("tooltip layer", () => {
  it("shows a reading while the pointer is over a target and drops it on leave", async () => {
    const user = userEvent.setup();
    render(
      <TooltipLayer>
        <Target at={{ x: 100, y: 300, pointerType: "mouse" }} />
      </TooltipLayer>,
    );

    await user.hover(screen.getByRole("button"));
    expect(tip()).not.toBeNull();
    expect(tip()!.textContent).toBe("09:0012 条");
    // Purely decorative: the same text is already on the target's own label.
    expect(tip()!.getAttribute("aria-hidden")).toBe("true");

    await user.unhover(screen.getByRole("button"));
    expect(tip()).toBeNull();
  });

  it("keeps out of the way of touch, where a tip would cover what it explains", async () => {
    const user = userEvent.setup();
    render(
      <TooltipLayer>
        <Target at={{ x: 100, y: 300, pointerType: "touch" }} />
      </TooltipLayer>,
    );

    await user.hover(screen.getByRole("button"));
    expect(tip()).toBeNull();
  });

  it("sits above and right of the pointer by default", async () => {
    const user = userEvent.setup();
    render(
      <TooltipLayer>
        <Target at={{ x: 100, y: 300, pointerType: "mouse" }} />
      </TooltipLayer>,
    );

    await user.hover(screen.getByRole("button"));
    expect(tip()!.style.left).toBe("100px");
    expect(tip()!.style.top).toBe("300px");
    expect(tip()!.style.transform).toBe("translate(12px, calc(-100% - 12px))");
  });

  it("flips near the right edge and near the top so the tip stays on screen", async () => {
    const user = userEvent.setup();
    render(
      <TooltipLayer>
        <Target at={{ x: window.innerWidth - 8, y: 10, pointerType: "mouse" }} />
      </TooltipLayer>,
    );

    await user.hover(screen.getByRole("button"));
    expect(tip()!.style.transform).toBe("translate(calc(-100% - 12px), 12px)");
  });

  it("drops the tip when the page scrolls out from under the pointer", async () => {
    const user = userEvent.setup();
    render(
      <TooltipLayer>
        <Target at={{ x: 100, y: 300, pointerType: "mouse" }} />
      </TooltipLayer>,
    );

    await user.hover(screen.getByRole("button"));
    expect(tip()).not.toBeNull();

    act(() => {
      window.dispatchEvent(new Event("scroll"));
    });
    expect(tip()).toBeNull();
  });

  it("is inert outside a layer, so a component can be reused without one", async () => {
    const user = userEvent.setup();
    render(<Target at={{ x: 100, y: 300, pointerType: "mouse" }} />);

    await user.hover(screen.getByRole("button"));
    expect(tip()).toBeNull();
  });
});
