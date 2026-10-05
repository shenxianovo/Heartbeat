"use client";

import { useEffect, useRef, useState } from "react";
import { pack } from "./packBubbles";

export interface BubbleItem {
  id: string;
  label: string;
  detail: string;
  accessibleLabel: string;
  weight: number;
  color: string;
  borderWidth?: number;
}

export function BubbleMap({
  items,
  label,
  selected,
  onSelect,
}: {
  items: BubbleItem[];
  label: string;
  selected?: string | null;
  onSelect?: (id: string) => void;
}) {
  const element = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  useEffect(() => {
    const observer = new ResizeObserver(([entry]) => entry && setWidth(entry.contentRect.width));
    if (element.current) observer.observe(element.current);
    return () => observer.disconnect();
  }, []);
  const { circles, height } = pack(items, width);
  return (
    <div ref={element} className="activity-bubbles" style={{ height }} aria-label={label}>
      {circles.map(({ item, x, y, radius }) => {
        const props = {
          className: "activity-bubble",
          "aria-label": item.accessibleLabel,
          title: item.accessibleLabel,
          style: {
            left: x - radius,
            top: y - radius,
            width: radius * 2,
            height: radius * 2,
            background: item.color,
            borderWidth: item.borderWidth ?? 1,
          },
          children:
            radius > 38 ? (
              <>
                <strong>{item.label}</strong>
                <span>{item.detail}</span>
              </>
            ) : null,
        };
        return (
          <button
            key={item.id}
            type="button"
            aria-pressed={selected === item.id}
            onClick={() => onSelect?.(item.id)}
            {...props}
          />
        );
      })}
    </div>
  );
}
