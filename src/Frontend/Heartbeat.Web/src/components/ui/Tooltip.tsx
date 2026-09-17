"use client";

import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

/** Enough of a pointer event to place a tip, so callers can pass synthetic events in tests. */
export interface TooltipPointer {
  clientX: number;
  clientY: number;
  pointerType?: string;
}

export interface TooltipHandle {
  show: (content: ReactNode, pointer: TooltipPointer) => void;
  hide: () => void;
}

interface Tip {
  content: ReactNode;
  x: number;
  y: number;
}

const inert: TooltipHandle = { show: () => {}, hide: () => {} };
const TooltipContext = createContext<TooltipHandle>(inert);

/** Outside a layer this is inert, so a component can be reused without dragging the layer along. */
export function useTooltip(): TooltipHandle {
  return useContext(TooltipContext);
}

const GAP = 12;
const ASSUMED_WIDTH = 240;
const ASSUMED_HEIGHT = 72;

/**
 * Sits above and right of the pointer, flipping near a viewport edge so the tip stays whole.
 * Flipping on the pointer position rather than on a measured rect keeps this a pure function:
 * one render, no reflow, no jump on the first frame.
 */
function place(tip: Tip): { left: number; top: number; transform: string } {
  const viewport = { width: window.innerWidth, height: window.innerHeight };
  const x = tip.x + ASSUMED_WIDTH + GAP > viewport.width ? `calc(-100% - ${GAP}px)` : `${GAP}px`;
  const y = tip.y - ASSUMED_HEIGHT - GAP < 0 ? `${GAP}px` : `calc(-100% - ${GAP}px)`;
  return { left: tip.x, top: tip.y, transform: `translate(${x}, ${y})` };
}

/**
 * Hosts the one hover tip shared by everything inside it. A single portal to the body keeps tips out
 * of the clipped, blurred lane containers that would otherwise cut them off, and guarantees only one
 * tip is on screen at a time.
 */
export function TooltipLayer({ children }: { children: ReactNode }) {
  const [tip, setTip] = useState<Tip | null>(null);
  const open = tip !== null;
  useEffect(() => {
    if (!open) return;
    const hide = () => setTip(null);
    // Scrolling or resizing moves the target out from under a tip anchored to old pointer coordinates.
    window.addEventListener("scroll", hide, true);
    window.addEventListener("resize", hide);
    return () => {
      window.removeEventListener("scroll", hide, true);
      window.removeEventListener("resize", hide);
    };
  }, [open]);
  const handle = useMemo<TooltipHandle>(
    () => ({
      show: (content, pointer) => {
        // A tip under a fingertip hides what it explains; touch and pen users get the aria label.
        if (pointer.pointerType && pointer.pointerType !== "mouse") setTip(null);
        else setTip({ content, x: pointer.clientX, y: pointer.clientY });
      },
      hide: () => setTip(null),
    }),
    [],
  );
  return (
    <TooltipContext.Provider value={handle}>
      {children}
      {/* No tip exists until a pointer asks for one, so the server renders nothing here. */}
      {tip
        ? createPortal(
            <div className="ui-tooltip" role="presentation" aria-hidden="true" style={place(tip)}>
              {tip.content}
            </div>,
            document.body,
          )
        : null}
    </TooltipContext.Provider>
  );
}

/** A tip with a quiet caption above its value, the shape used by the timeline lanes. */
export function TooltipReading({ caption, value }: { caption: string; value: ReactNode }) {
  return (
    <>
      <span className="ui-tooltip-caption">{caption}</span>
      <strong>{value}</strong>
    </>
  );
}
