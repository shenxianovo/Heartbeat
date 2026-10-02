"use client";

import { Icon } from "@/components/ui/Icon";
import { useTheme } from "@/lib/theme";

/** Login keeps its theme control available before the session navigation exists. */
export function ThemeToggle() {
  const { isDark, toggle } = useTheme();
  return (
    <button
      type="button"
      className="theme-toggle theme-toggle--floating"
      title={isDark ? "切换到浅色" : "切换到深色"}
      aria-label={isDark ? "切换到浅色" : "切换到深色"}
      onClick={toggle}
    >
      <Icon name={isDark ? "sun" : "moon"} size={18} />
    </button>
  );
}
