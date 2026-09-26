"use client";

import { useSyncExternalStore, type PropsWithChildren } from "react";

export type ThemeMode = "light" | "dark" | "system";

const STORAGE_KEY = "heartbeat-theme";

/**
 * Blocking snippet injected in <head> so the resolved theme is applied before
 * first paint. Mirrors the resolution in main's useTheme (system → matchMedia),
 * keeping SSR markup and the client's first frame in sync to avoid a flash.
 */
export const themeBootScript = `(()=>{try{var m=localStorage.getItem("${STORAGE_KEY}");if(m!=="light"&&m!=="dark"&&m!=="system")m="system";var d=m==="system"?matchMedia("(prefers-color-scheme: dark)").matches:m==="dark";document.documentElement.classList.toggle("dark",d);}catch(e){}})();`;

interface Snapshot {
  mode: ThemeMode;
  isDark: boolean;
}

// The store lives outside React: it is an external system (localStorage +
// prefers-color-scheme + the <html> class), so useSyncExternalStore is the
// correct bridge and there is no setState inside an effect.
const SERVER_SNAPSHOT: Snapshot = { mode: "system", isDark: false };

let snapshot: Snapshot = SERVER_SNAPSHOT;
let initialized = false;
const listeners = new Set<() => void>();

function systemPrefersDark(): boolean {
  return typeof window !== "undefined" && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function resolve(mode: ThemeMode): boolean {
  return mode === "system" ? systemPrefersDark() : mode === "dark";
}

function readStored(): ThemeMode {
  if (typeof window === "undefined") return "system";
  const v = window.localStorage.getItem(STORAGE_KEY);
  return v === "light" || v === "dark" || v === "system" ? v : "system";
}

function applyDom(isDark: boolean): void {
  if (typeof document !== "undefined") {
    document.documentElement.classList.toggle("dark", isDark);
  }
}

function emit(): void {
  for (const listener of listeners) listener();
}

function commit(mode: ThemeMode): void {
  const isDark = resolve(mode);
  if (snapshot.mode === mode && snapshot.isDark === isDark) {
    applyDom(isDark);
    return;
  }
  snapshot = { mode, isDark };
  applyDom(isDark);
  emit();
}

function persist(mode: ThemeMode): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, mode);
  } catch {
    /* storage unavailable — theme still applies for this session */
  }
}

function ensureInitialized(): void {
  if (initialized || typeof window === "undefined") return;
  initialized = true;
  commit(readStored());
  // Follow the OS while in system mode.
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if (snapshot.mode === "system") commit("system");
  });
}

function subscribe(listener: () => void): () => void {
  ensureInitialized();
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function setMode(mode: ThemeMode): void {
  persist(mode);
  commit(mode);
}

export function toggleTheme(): void {
  setMode(snapshot.isDark ? "light" : "dark");
}

// ThemeProvider is kept for shell structure/parity with main's App; the store
// itself is module-level, so the provider only renders its children.
export function ThemeProvider({ children }: PropsWithChildren) {
  return <>{children}</>;
}

interface ThemeValue extends Snapshot {
  toggle: () => void;
  setMode: (mode: ThemeMode) => void;
}

export function useTheme(): ThemeValue {
  const current = useSyncExternalStore(
    subscribe,
    () => snapshot,
    () => SERVER_SNAPSHOT,
  );
  return { ...current, toggle: toggleTheme, setMode };
}
