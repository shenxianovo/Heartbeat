// 只在本机浏览器里记住的偏好。读写失败时使用默认值。

export type Theme = "system" | "light" | "dark";
/** 基调：中性灰阶，或取自吉祥物水手服的天蓝。原型阶段两种都留着对照。 */
export type Tone = "gray" | "sky";
export type ViewKey = "bubble" | "strip" | "ring";

export const VIEWS: readonly ViewKey[] = ["bubble", "strip", "ring"];

const THEME_KEY = "orisumi.theme";
const VIEW_KEY = "orisumi.view";
const TONE_KEY = "orisumi.tone";

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

export function readTone(): Tone {
  return read(TONE_KEY) === "sky" ? "sky" : "gray";
}

export function applyTone(tone: Tone): void {
  if (tone === "gray") document.documentElement.removeAttribute("data-tone");
  else document.documentElement.setAttribute("data-tone", tone);
}

export function saveTone(tone: Tone): void {
  write(TONE_KEY, tone);
  applyTone(tone);
}

export function isView(value: unknown): value is ViewKey {
  return typeof value === "string" && (VIEWS as readonly string[]).includes(value);
}

export function readView(): ViewKey {
  const value = read(VIEW_KEY);
  return isView(value) ? value : "bubble";
}

export function saveView(view: ViewKey): void {
  write(VIEW_KEY, view);
}
