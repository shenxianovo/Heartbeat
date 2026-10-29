import { createContext, useContext, type ReactNode } from "react";
import type { OrisumiSource } from "./client";

const SourceContext = createContext<OrisumiSource | null>(null);

export function SourceProvider({ source, children }: { source: OrisumiSource; children: ReactNode }) {
  return <SourceContext.Provider value={source}>{children}</SourceContext.Provider>;
}

export function useSource(): OrisumiSource {
  const source = useContext(SourceContext);
  if (!source) throw new Error("useSource 需要在 SourceProvider 内使用");
  return source;
}
