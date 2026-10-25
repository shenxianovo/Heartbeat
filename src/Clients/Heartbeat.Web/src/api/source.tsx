import { createContext, useContext, type ReactNode } from "react";
import { createHttpSource, type HeartbeatSource } from "./client";
import { createDemoSource } from "./demo";

export type SourceKind = "api" | "demo";

const STORAGE_KEY = "heartbeat.source";

/** `?source=demo` 切到演示数据源，`?source=api` 切回；选择在本标签页内保持。 */
export function chooseSourceKind(search: string, storage: Pick<Storage, "getItem" | "setItem"> | null): SourceKind {
  const requested = new URLSearchParams(search).get("source");
  if (requested === "demo" || requested === "api") {
    try {
      storage?.setItem(STORAGE_KEY, requested);
    } catch {
      // 存不下时只影响刷新后的选择。
    }
    return requested;
  }
  try {
    return storage?.getItem(STORAGE_KEY) === "demo" ? "demo" : "api";
  } catch {
    return "api";
  }
}

function sessionStore(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function createSource(kind: SourceKind): HeartbeatSource {
  return kind === "demo" ? createDemoSource() : createHttpSource();
}

export function initialSourceKind(): SourceKind {
  return chooseSourceKind(window.location.search, sessionStore());
}

const SourceContext = createContext<{ kind: SourceKind; source: HeartbeatSource } | null>(null);

export function SourceProvider({
  kind,
  source,
  children,
}: {
  kind: SourceKind;
  source: HeartbeatSource;
  children: ReactNode;
}) {
  return <SourceContext.Provider value={{ kind, source }}>{children}</SourceContext.Provider>;
}

export function useSource(): HeartbeatSource {
  const value = useContext(SourceContext);
  if (!value) throw new Error("useSource 需要在 SourceProvider 内使用");
  return value.source;
}

export function useSourceKind(): SourceKind {
  return useContext(SourceContext)?.kind ?? "api";
}
