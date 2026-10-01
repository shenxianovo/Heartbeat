"use client";

import { Icon } from "@/components/ui/Icon";
import { useTheme } from "@/lib/theme";

/** Sits in the page header; pages without one (login) pin it to the corner instead. */
export function ThemeToggle({ floating = false }: { floating?: boolean }) {
  const { isDark, toggle } = useTheme();
  return (
    <button
      type="button"
      className={`theme-toggle${floating ? " theme-toggle--floating" : ""}`}
      title={isDark ? "切换到浅色" : "切换到深色"}
      aria-label={isDark ? "切换到浅色" : "切换到深色"}
      onClick={toggle}
    >
      <Icon name={isDark ? "sun" : "moon"} size={18} />
    </button>
  );
}
