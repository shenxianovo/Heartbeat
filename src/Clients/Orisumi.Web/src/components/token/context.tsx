import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { useNavigate, useSearch } from "@tanstack/react-router";

// 两件跨页面的状态：悬停中的实体（同一实体的记号一起亮起），以及右侧堆叠的面板（写在 URL 的 stack 里）。

interface Highlight {
  active: string | null;
  set: (entityId: string | null) => void;
}

const HighlightContext = createContext<Highlight>({ active: null, set: () => undefined });

export function HighlightProvider({ children }: { children: ReactNode }) {
  const [active, setActive] = useState<string | null>(null);
  const value = useMemo(() => ({ active, set: setActive }), [active]);
  return <HighlightContext.Provider value={value}>{children}</HighlightContext.Provider>;
}

export function useHighlight(): Highlight {
  return useContext(HighlightContext);
}

/** 记号所在的面板层级。页面上是 -1，第一个面板是 0。 */
const DepthContext = createContext(-1);

export function PanelDepth({ depth, children }: { depth: number; children: ReactNode }) {
  return <DepthContext.Provider value={depth}>{children}</DepthContext.Provider>;
}

export function useDepth(): number {
  return useContext(DepthContext);
}

export function parseStack(value: unknown): string[] {
  return typeof value === "string" && value ? value.split(",").filter(Boolean) : [];
}

/** 面板栈：从第 depth 层的记号打开一个实体时，丢掉它后面的面板，再把这个实体放到最后。 */
export function useStack() {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { stack?: string };
  const ids = useMemo(() => parseStack(search.stack), [search.stack]);

  const write = useCallback(
    (next: string[]) =>
      void navigate({
        to: ".",
        search: ((prev: Record<string, unknown>) => ({
          ...prev,
          stack: next.length ? next.join(",") : undefined,
        })) as never,
        replace: false,
      }),
    [navigate],
  );

  const open = useCallback(
    (entityId: string, depth: number) => {
      if (ids[depth + 1] === entityId && ids.length === depth + 2) return;
      write([...ids.slice(0, depth + 1), entityId]);
    },
    [ids, write],
  );
  const close = useCallback((index: number) => write(ids.slice(0, index)), [ids, write]);

  return { ids, open, close };
}
