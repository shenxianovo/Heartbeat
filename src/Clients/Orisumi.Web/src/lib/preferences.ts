// 只在本机浏览器里记住的偏好。读写失败时使用默认值。

export type Theme = "system" | "light" | "dark";
export type ViewKey = "ecg" | "bubble" | "strip" | "ring";

export const VIEWS: readonly ViewKey[] = ["ecg", "bubble", "strip", "ring"];

const THEME_KEY = "orisumi.theme";
const VIEW_KEY = "orisumi.view";

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // 存不下时只影响下次打开。
  }
}

export function readTheme(): Theme {
  const value = read(THEME_KEY);
  return value === "light" || value === "dark" ? value : "system";
}

export function applyTheme(theme: Theme): void {
  if (theme === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
}

export function saveTheme(theme: Theme): void {
  write(THEME_KEY, theme);
  applyTheme(theme);
}

export function isView(value: unknown): value is ViewKey {
  return typeof value === "string" && (VIEWS as readonly string[]).includes(value);
}

export function readView(): ViewKey {
  const value = read(VIEW_KEY);
  return isView(value) ? value : "ecg";
}

export function saveView(view: ViewKey): void {
  write(VIEW_KEY, view);
}
