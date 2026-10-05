"use client";

import { useTheme } from "@/lib/theme";

/**
 * Mirrored mascot pinned bottom-left — the ambient anchor of main's look.
 * Decorative only: aria-hidden and non-interactive. Dims further in dark mode.
 */
export function MascotBackground() {
  const { isDark } = useTheme();
  return (
    // eslint-disable-next-line @next/next/no-img-element -- decorative, fixed background art
    <img
      src="/mascot.png"
      alt=""
      aria-hidden="true"
      className={`mascot${isDark ? " mascot--dark" : ""}`}
    />
  );
}
