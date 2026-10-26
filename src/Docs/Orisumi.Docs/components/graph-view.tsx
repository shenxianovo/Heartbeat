'use client';

// Adapted from Fumadocs Graph View (MIT):
// https://github.com/fuma-nama/fumadocs/blob/main/apps/docs/components/graph-view.tsx
/*
MIT License
Copyright (c) 2023 Fuma

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
import dynamic from 'next/dynamic';
import { forceCollide, forceLink, forceManyBody } from 'd3-force';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useTheme } from 'next-themes';
import type { ForceGraphMethods, NodeObject } from 'react-force-graph-2d';
import { sectionOf, type Graph, type GraphNode } from '@/lib/build-graph';

const ForceGraph2D = dynamic(() => import('react-force-graph-2d'), { ssr: false }) as typeof import('react-force-graph-2d').default;
type Node = NodeObject<GraphNode>;

const minRadius = 3;
const maxRadius = 16;

export function GraphView({ graph }: { graph: Graph }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const graphRef = useRef<ForceGraphMethods<GraphNode> | undefined>(undefined);
  const fitted = useRef(false);
  const [size, setSize] = useState<{ width: number; height: number }>();
  const [hovered, setHovered] = useState<Node | null>(null);
  const router = useRouter();
  const { resolvedTheme } = useTheme();
  const dark = resolvedTheme === 'dark';
  // The force simulation replaces link ends with node objects; the clone keeps `graph` intact for the lookups below.
  const data = useMemo(() => structuredClone(graph), [graph]);
  const neighbors = useMemo(() => new Map(graph.nodes.map((node) => [node.id, new Set(
    graph.links.flatMap((link) => link.source === node.id ? [link.target] : link.target === node.id ? [link.source] : []),
  )])), [graph]);
  const degrees = useMemo(() => new Map(graph.nodes.map((node) => [node.id, {
    incoming: graph.links.filter((link) => link.target === node.id).length,
    outgoing: graph.links.filter((link) => link.source === node.id).length,
  }])), [graph]);
  // Pages referenced by more pages are drawn larger. Radii span the same range however many pages there are: the most
  // referenced page gets the largest, an unreferenced page the smallest, and the area grows with the reference count.
  const mostReferenced = useMemo(() => Math.max(1, ...[...degrees.values()].map((degree) => degree.incoming)), [degrees]);
  const radius = (node: Node) => minRadius
    + (maxRadius - minRadius) * Math.sqrt(degrees.get(node.id)!.incoming / mostReferenced)
    + (hovered?.id === node.id ? 2 : 0);

  useEffect(() => {
    const observer = new ResizeObserver(([entry]) => {
      setSize({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(containerRef.current!);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (size && fitted.current) graphRef.current?.zoomToFit(0, 45);
  }, [size]);

  const graphHandle = useMemo(() => ({
    get current() { return graphRef.current; },
    set current(instance: ForceGraphMethods<GraphNode> | undefined) {
      graphRef.current = instance;
      if (!instance) return;
      instance.d3Force('link', forceLink().distance(180));
      instance.d3Force('charge', forceManyBody().strength(-220));
      instance.d3Force('collision', forceCollide(65));
    },
  }), []);

  const fit = () => graphRef.current?.zoomToFit(300, 45);
  const connected = (node: Node) => !hovered || hovered.id === node.id || neighbors.get(hovered.id)?.has(node.id);

  return (
    <div ref={containerRef} className="relative h-[70vh] min-h-[500px] max-h-[800px] overflow-hidden rounded-xl border bg-fd-card sm:min-h-[620px]" role="group" aria-label="文档引用图">
      {size && (
        <ForceGraph2D<GraphNode>
          ref={graphHandle}
          width={size.width}
          height={size.height}
          graphData={data}
          nodeLabel={() => ''}
          nodeCanvasObject={(node, ctx, scale) => {
            const active = connected(node);
            const fontSize = Math.max(12, 11 / scale);
            ctx.globalAlpha = active ? 1 : 0.2;
            ctx.beginPath();
            const r = radius(node);
            ctx.arc(node.x!, node.y!, r, 0, Math.PI * 2);
            ctx.fillStyle = sectionOf(graph, node).color;
            ctx.fill();
            ctx.font = `${fontSize}px sans-serif`;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';
            ctx.fillStyle = dark ? '#e2e8f0' : '#334155';
            ctx.fillText(node.text, node.x!, node.y! + r + 4);
            ctx.globalAlpha = 1;
          }}
          nodePointerAreaPaint={(node, color, ctx, scale) => {
            const fontSize = Math.max(12, 11 / scale);
            ctx.font = `${fontSize}px sans-serif`;
            const r = radius(node);
            const width = Math.max(2 * r, ctx.measureText(node.text).width);
            ctx.fillStyle = color;
            ctx.fillRect(node.x! - width / 2, node.y! - r, width, 2 * r + fontSize + 6);
          }}
          linkColor={(link) => {
            const source = typeof link.source === 'object' ? link.source.id : link.source;
            const target = typeof link.target === 'object' ? link.target.id : link.target;
            return hovered && (source === hovered.id || target === hovered.id)
              ? (dark ? '#c4b5fd' : '#7c3aed')
              : (dark ? '#64748b55' : '#94a3b844');
          }}
          linkDirectionalArrowLength={4}
          linkDirectionalArrowRelPos={0.9}
          linkWidth={1}
          warmupTicks={120}
          cooldownTicks={60}
          onEngineStop={() => {
            if (fitted.current) return;
            fitted.current = true;
            fit();
          }}
          onNodeHover={setHovered}
          onNodeClick={(node) => router.push(node.url)}
          enableNodeDrag
          enableZoomInteraction
        />
      )}
      <button type="button" onClick={fit} className="absolute right-3 top-3 rounded-lg border bg-fd-background px-3 py-1.5 text-sm shadow-sm hover:bg-fd-accent">
        适应画布
      </button>
      {hovered && (
        <div className="pointer-events-none absolute bottom-3 left-3 right-3 rounded-lg border bg-fd-popover p-3 text-sm text-fd-popover-foreground shadow-sm" aria-live="polite">
          <div className="font-medium">{sectionOf(graph, hovered).name} · {hovered.text}</div>
          <div className="mt-1 text-fd-muted-foreground">{hovered.description}</div>
          <div className="mt-1 text-xs text-fd-muted-foreground">
            被 {degrees.get(hovered.id)!.incoming} 页引用 · 引用 {degrees.get(hovered.id)!.outgoing} 页
          </div>
        </div>
      )}
    </div>
  );
}
