"use client";

import { Icon } from "@/components/ui/Icon";
import { useTheme } from "@/lib/theme";

export function ThemeToggle() {
  const { isDark, toggle } = useTheme();
  return (
    <button
      type="button"
      className="theme-toggle"
      title={isDark ? "切换到浅色" : "切换到深色"}
      aria-label={isDark ? "切换到浅色" : "切换到深色"}
      onClick={toggle}
    >
      <Icon name={isDark ? "sun" : "moon"} size={18} />
    </button>
  );
}
