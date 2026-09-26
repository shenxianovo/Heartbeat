"use client";

import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button } from "./button";

interface Props {
  label: string;
  trigger: ReactNode;
  children: (close: () => void) => ReactNode;
  className?: string;
}

export function Popover({ label, trigger, children, className = "" }: Props) {
  const [open, setOpen] = useState(false);
  const [returnFocus, setReturnFocus] = useState(false);
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  function close() {
    setReturnFocus(true);
    setOpen(false);
  }
  useLayoutEffect(() => {
    if (!open && returnFocus)
      root.current?.querySelector<HTMLButtonElement>(".popover-trigger")?.focus();
  }, [open, returnFocus]);
  useLayoutEffect(() => {
    const element = panel.current;
    if (!open || !element) return;
    function position() {
      if (!element) return;
      element.style.translate = "none";
      const rect = element.getBoundingClientRect();
      const x = Math.max(12 - rect.left, Math.min(0, window.innerWidth - 12 - rect.right));
      const y =
        rect.bottom > window.innerHeight - 12 && rect.top - rect.height - 48 >= 12
          ? -rect.height - 48
          : 0;
      element.style.translate = `${x}px ${y}px`;
    }
    position();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(position);
    observer?.observe(element);
    window.addEventListener("resize", position);
    const focus =
      element.querySelector<HTMLElement>("[data-autofocus]") ??
      element.querySelector<HTMLElement>(
        "button:not(:disabled), input:not(:disabled), [tabindex='0']",
      );
    focus?.focus({ preventScroll: true });
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", position);
    };
  }, [open]);
  useEffect(() => {
    if (!open) return;
    function outside(event: Event) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) {
        setReturnFocus(false);
        setOpen(false);
      }
    }
    document.addEventListener("pointerdown", outside);
    document.addEventListener("focusin", outside);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("focusin", outside);
    };
  }, [open]);
  return (
    <div
      ref={root}
      className={`ui-popover ${className}`}
      onKeyDown={(event) => {
        if (event.key === "Escape" && open) {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      }}
    >
      <Button
        className="popover-trigger"
        aria-label={label}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        onClick={() => {
          setReturnFocus(false);
          setOpen(!open);
        }}
      >
        {trigger}
      </Button>
      {open ? (
        <div ref={panel} id={id} className="ui-popover-content" role="dialog" aria-label={label}>
          {children(close)}
        </div>
      ) : null}
    </div>
  );
}
