'use client';

import { Suspense, use, useId, useSyncExternalStore } from 'react';
import { useTheme } from 'next-themes';

const cache = new Map<string, Promise<unknown>>();

function cachePromise<T>(key: string, create: () => Promise<T>): Promise<T> {
  const existing = cache.get(key);
  if (existing) return existing as Promise<T>;
  const promise = create();
  cache.set(key, promise);
  return promise;
}

export function Mermaid({ chart }: { chart: string }) {
  const isClient = useSyncExternalStore(
    () => () => {},
    () => true,
    () => false,
  );

  if (!isClient) return null;
  return (
    <Suspense fallback={null}>
      <MermaidContent chart={chart} />
    </Suspense>
  );
}

function MermaidContent({ chart }: { chart: string }) {
  const id = useId();
  const { resolvedTheme } = useTheme();
  const { default: mermaid } = use(cachePromise('mermaid', () => import('mermaid')));

  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'loose',
    fontFamily: 'inherit',
    look: 'handDrawn',
    theme: resolvedTheme === 'dark' ? 'dark' : 'default',
  });

  const { svg, bindFunctions } = use(
    cachePromise(`${chart}-${resolvedTheme}`, () => mermaid.render(id, chart)),
  );

  return (
    <div
      className="my-6 overflow-x-auto"
      ref={(container) => { if (container) bindFunctions?.(container); }}
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
}
