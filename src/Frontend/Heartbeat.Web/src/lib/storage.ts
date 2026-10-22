import { useCallback, useEffect, useState } from "react";

/**
 * 访问历史与已登记 Schema：后端暂无列表接口，用 localStorage 保留用户的实体足迹。
 * 仅保存 ID + 由读取结果得到的摘要，不放完整信封。
 */
const HISTORY_KEY = "heartbeat:entity-history:v1";
const SCHEMAS_KEY = "heartbeat:schema-bookmarks:v1";
const THEME_KEY = "heartbeat:theme:v1";
const MASCOT_KEY = "heartbeat:mascot:v1";
const LIMIT = 24;

export interface HistoryEntry {
  id: string;
  category: string;
  label: string | null;
  visitedAt: number;
}

function read<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function write(key: string, value: unknown) {
  localStorage.setItem(key, JSON.stringify(value));
  window.dispatchEvent(new CustomEvent("heartbeat:storage", { detail: { key } }));
}

export function useEntityHistory() {
  const [history, setHistory] = useState<HistoryEntry[]>(() => read(HISTORY_KEY, []));
  useEffect(() => {
    const onStorage = (e: StorageEvent | Event) => {
      const key = e instanceof StorageEvent ? e.key : (e as CustomEvent<{ key: string }>).detail.key;
      if (key === HISTORY_KEY) setHistory(read(HISTORY_KEY, []));
    };
    window.addEventListener("storage", onStorage);
    window.addEventListener("heartbeat:storage", onStorage);
    return () => {
      window.removeEventListener("storage", onStorage);
      window.removeEventListener("heartbeat:storage", onStorage);
    };
  }, []);

  const push = useCallback((entry: Omit<HistoryEntry, "visitedAt">) => {
    const next = [
      { ...entry, visitedAt: Date.now() },
      ...read<HistoryEntry[]>(HISTORY_KEY, []).filter((h) => h.id !== entry.id),
    ].slice(0, LIMIT);
    write(HISTORY_KEY, next);
  }, []);

  const remove = useCallback((id: string) => {
    write(
      HISTORY_KEY,
      read<HistoryEntry[]>(HISTORY_KEY, []).filter((h) => h.id !== id),
    );
  }, []);

  const clear = useCallback(() => write(HISTORY_KEY, []), []);
  return { history, push, remove, clear };
}

export function useSchemaBookmarks() {
  const [ids, setIds] = useState<string[]>(() => read(SCHEMAS_KEY, []));
  useEffect(() => {
    const onStorage = (e: StorageEvent | Event) => {
      const key = e instanceof StorageEvent ? e.key : (e as CustomEvent<{ key: string }>).detail.key;
      if (key === SCHEMAS_KEY) setIds(read(SCHEMAS_KEY, []));
    };
    window.addEventListener("storage", onStorage);
    window.addEventListener("heartbeat:storage", onStorage);
    return () => {
      window.removeEventListener("storage", onStorage);
      window.removeEventListener("heartbeat:storage", onStorage);
    };
  }, []);

  const add = useCallback((id: string) => {
    if (!read<string[]>(SCHEMAS_KEY, []).includes(id)) write(SCHEMAS_KEY, [...read(SCHEMAS_KEY, []), id]);
  }, []);
  const remove = useCallback(
    (id: string) =>
      write(
        SCHEMAS_KEY,
        read(SCHEMAS_KEY, []).filter((x) => x !== id),
      ),
    [],
  );
  return { ids, add, remove };
}

export type MascotTheme = "windows" | "mac";
export type Mode = "light" | "dark";

export function useAppearance() {
  const [mascot, setMascotState] = useState<MascotTheme>(() => read<MascotTheme>(MASCOT_KEY, "windows"));
  const [mode, setModeState] = useState<Mode>(() => {
    const saved = read<Mode | null>(THEME_KEY, null);
    if (saved) return saved;
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  });

  useEffect(() => {
    document.documentElement.dataset.mascot = mascot;
    document.documentElement.classList.toggle("dark", mode === "dark");
  }, [mascot, mode]);

  const setMascot = (m: MascotTheme) => {
    write(MASCOT_KEY, m);
    setMascotState(m);
  };
  const setMode = (m: Mode) => {
    write(THEME_KEY, m);
    setModeState(m);
  };
  const toggleMode = () => setMode(mode === "dark" ? "light" : "dark");

  return { mascot, mode, setMascot, setMode, toggleMode };
}
